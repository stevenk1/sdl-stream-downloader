using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SDL.Configuration;
using System.Globalization;
using SDL.Models;

namespace SDL.Services;

public partial class VideoConversionService
{
    private readonly VideoStorageSettings _settings;
    private readonly ILogger<VideoConversionService> _logger;
    private readonly VideoDatabaseService _db;
    private readonly IFileSystemService _fileSystem;
    private readonly Dictionary<string, (Process Process, CancellationTokenSource Cts)> _activeConversions = new();

    public event EventHandler<ConversionJob>? ConversionUpdated;

    public VideoConversionService(IOptions<VideoStorageSettings> settings, ILogger<VideoConversionService> logger, VideoDatabaseService db, IFileSystemService fileSystem)
    {
        _settings = settings.Value;
        _logger = logger;
        _db = db;
        _fileSystem = fileSystem;

        // Log loaded settings for diagnostics
        _logger.LogInformation("VideoConversionService initialized with settings:");
        _logger.LogInformation("  FfmpegPath: {FfmpegPath}", _settings.FfmpegPath);
        _logger.LogInformation("  VideoCodec: {VideoCodec}", _settings.VideoCodec);
        _logger.LogInformation("  AudioCodec: {AudioCodec}", _settings.AudioCodec);
        _logger.LogInformation("  VideoCrf: {VideoCrf}", _settings.VideoCrf);
        _logger.LogInformation("  VideoPreset: {VideoPreset}", _settings.VideoPreset);
        _logger.LogInformation("  AudioBitrate: {AudioBitrate}", _settings.AudioBitrate);
        _logger.LogInformation("  ConversionOutputFormat: {ConversionOutputFormat}", _settings.ConversionOutputFormat);

        // Ensure converted directory exists
        _fileSystem.CreateDirectory(_settings.ConvertedDirectory);

        // Ensure thumbnail directory exists
        _fileSystem.CreateDirectory(_settings.ThumbnailDirectory);
    }

    public IEnumerable<ConversionJob> GetActiveConversions() => _db.GetActiveConversionJobs();

    public ConversionJob? GetConversionByDownloadJobId(string downloadJobId)
    {
        return _db.GetConversionByDownloadJobId(downloadJobId);
    }

    public void RemoveConversionJob(string jobId)
    {
        _db.DeleteConversionJob(jobId);
        _logger.LogInformation("Removed conversion job {JobId} from database", jobId);
    }

    public async Task<ConversionJob> StartConversionAsync(DownloadJob downloadJob)
    {
        var job = new ConversionJob
        {
            SourcePath = downloadJob.OutputPath,
            Title = downloadJob.Title,
            OriginalUrl = downloadJob.Url,
            DownloadJobId = downloadJob.Id,
            Status = ConversionStatus.Queued
        };

        // Generate output path
        var fileName = _fileSystem.GetFileNameWithoutExtension(downloadJob.OutputPath);
        job.OutputPath = _fileSystem.CombinePaths(_settings.ConvertedDirectory, $"{fileName}.{_settings.ConversionOutputFormat}");

        _db.UpsertConversionJob(job);
        NotifyConversionUpdated(job);

        var cts = new CancellationTokenSource();
        _ = Task.Run(() => ExecuteConversionAsync(job, cts.Token), cts.Token);

        return job;
    }

    public async Task<bool> CancelConversionAsync(string jobId)
    {
        if (!_activeConversions.TryGetValue(jobId, out var conversion))
            return false;

        try
        {
            conversion.Cts.Cancel();

            if (!conversion.Process.HasExited)
            {
                conversion.Process.Kill(entireProcessTree: true);
                await conversion.Process.WaitForExitAsync();
            }

            _activeConversions.Remove(jobId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error canceling conversion {JobId}", jobId);
            return false;
        }
    }

    private async Task ExecuteConversionAsync(ConversionJob job, CancellationToken cancellationToken)
    {
        try
        {
            // Build FFmpeg arguments
            var args = $"-i \"{job.SourcePath}\" " +
                      $"-c:v {_settings.VideoCodec} " +
                      $"-c:a {_settings.AudioCodec} " +
                      $"-b:a {_settings.AudioBitrate} " +
                      $"-progress pipe:1 -stats_period 0.5 " +
                      $"-y " + // Overwrite output file
                      $"\"{job.OutputPath}\"";

            // Log the command for manual testing
            _logger.LogInformation("Executing FFmpeg command: {FfmpegPath} {Args}", _settings.FfmpegPath, args);

            var processInfo = new ProcessStartInfo
            {
                FileName = _settings.FfmpegPath,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var process = new Process { StartInfo = processInfo };
            var cts = new CancellationTokenSource();
            _activeConversions[job.Id] = (process, cts);

            // Get duration from ffprobe first for accurate progress
            double? totalDuration = await GetVideoDurationAsync(job.SourcePath);

            process.OutputDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    _logger.LogInformation("FFmpeg stdout: {Data}", e.Data);
                    ParseFfmpegProgress(job, e.Data, totalDuration);
                }
            };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    _logger.LogInformation("FFmpeg stderr: {Data}", e.Data);
                    // FFmpeg outputs progress info to stderr
                    ParseFfmpegProgress(job, e.Data, totalDuration);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            job.Status = ConversionStatus.Converting;
            NotifyConversionUpdated(job);

            await process.WaitForExitAsync(cancellationToken);

            // Determine final status
            if (cancellationToken.IsCancellationRequested || process.ExitCode == 137 || process.ExitCode == 143 || process.ExitCode == 255)
            {
                job.Status = ConversionStatus.Failed;
                job.ErrorMessage = "Conversion cancelled";

                // Clean up partial output file
                if (_fileSystem.FileExists(job.OutputPath))
                {
                    try { _fileSystem.FileDelete(job.OutputPath); } catch { }
                }

                // Update the corresponding DownloadJob status
                UpdateDownloadJobAfterConversion(job.DownloadJobId, DownloadStatus.ConversionFailed, "Conversion cancelled", null, null);
            }
            else if (process.ExitCode == 0 && _fileSystem.FileExists(job.OutputPath))
            {
                job.Status = ConversionStatus.Completed;
                job.Progress = 100;

                // Generate thumbnails for the converted video
                List<string>? thumbnailFileNames = null;
                try
                {
                    thumbnailFileNames = await GenerateMultipleThumbnailsAsync(job.OutputPath, job.Id);
                    if (thumbnailFileNames != null && thumbnailFileNames.Any())
                    {
                        _logger.LogInformation("Generated {Count} thumbnails for conversion job {JobId}",
                            thumbnailFileNames.Count, job.Id);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to generate thumbnail for conversion job {JobId}", job.Id);
                    // Don't fail the conversion if thumbnail generation fails
                }

                // Update the corresponding DownloadJob status
                UpdateDownloadJobAfterConversion(job.DownloadJobId, DownloadStatus.ConversionCompleted, null, thumbnailFileNames, job.OutputPath);
            }
            else
            {
                job.Status = ConversionStatus.Failed;
                job.ErrorMessage = $"Conversion failed with exit code {process.ExitCode}";

                // Update the corresponding DownloadJob status
                UpdateDownloadJobAfterConversion(job.DownloadJobId, DownloadStatus.ConversionFailed, job.ErrorMessage, null, null);
            }

            NotifyConversionUpdated(job);
            _activeConversions.Remove(job.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing conversion for job {JobId}", job.Id);
            job.Status = ConversionStatus.Failed;
            job.ErrorMessage = ex.Message;

            // Update the corresponding DownloadJob status
            UpdateDownloadJobAfterConversion(job.DownloadJobId, DownloadStatus.ConversionFailed, ex.Message, null, null);

            NotifyConversionUpdated(job);
            _activeConversions.Remove(job.Id);
        }
    }

    private void UpdateDownloadJobAfterConversion(string downloadJobId, DownloadStatus status, string? errorMessage, List<string>? thumbnails, string? convertedFilePath)
    {
        try
        {
            var downloadJob = _db.GetDownloadJob(downloadJobId);
            if (downloadJob != null)
            {
                downloadJob.Status = status;
                downloadJob.ErrorMessage = errorMessage;
                downloadJob.ConvertedFilePath = convertedFilePath;

                if (thumbnails != null && thumbnails.Any())
                {
                    downloadJob.Thumbnails = thumbnails;
                    downloadJob.Thumbnail = thumbnails.First();
                }

                _db.UpsertDownloadJob(downloadJob);
                _logger.LogInformation("Updated DownloadJob {JobId} status to {Status}, ConvertedFilePath: {ConvertedFilePath}",
                    downloadJobId, status, convertedFilePath ?? "null");
            }
            else
            {
                _logger.LogWarning("Could not find DownloadJob {JobId} to update after conversion", downloadJobId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating DownloadJob {JobId} after conversion", downloadJobId);
        }
    }

    private async Task<double?> GetVideoDurationAsync(string videoPath)
    {
        try
        {
            var processInfo = new ProcessStartInfo
            {
                FileName = "ffprobe",
                Arguments = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{videoPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var process = Process.Start(processInfo);
            if (process == null)
            {
                _logger.LogWarning("Failed to start ffprobe process for {VideoPath}", videoPath);
                return null;
            }

            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                _logger.LogWarning("ffprobe failed with exit code {ExitCode} for {VideoPath}. Error: {Error}",
                    process.ExitCode, videoPath, error);
                return null;
            }

            if (double.TryParse(output.Trim(), CultureInfo.InvariantCulture, out var duration))
            {
                _logger.LogInformation("Video duration for {VideoPath}: {Duration} seconds", videoPath, duration);
                return duration;
            }
            else
            {
                _logger.LogWarning("Could not parse duration from ffprobe output: {Output}", output);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting video duration for {VideoPath}", videoPath);
        }

        return null;
    }

    private void ParseFfmpegProgress(ConversionJob job, string output, double? totalDuration)
    {
        try
        {
            // FFmpeg with -progress outputs: out_time_ms=5670, out_time=00:00:05.670000, speed=1.2x, fps=45.67
            var timeMsMatch = TimeMillisecondsRegex().Match(output);
            var speedMatch = SpeedRegex().Match(output);
            var fpsMatch = FpsRegex().Match(output);

            if (timeMsMatch.Success && totalDuration.HasValue && totalDuration.Value > 0)
            {
                if (long.TryParse(timeMsMatch.Groups[1].Value, CultureInfo.InvariantCulture, out var timeMs))
                {
                    var currentTime = timeMs / 1_000_000.0; // Convert microseconds to seconds
                    job.Progress = Math.Min(100, (currentTime / totalDuration.Value) * 100);

                    // Parse speed and calculate ETA
                    if (speedMatch.Success && double.TryParse(speedMatch.Groups[1].Value, CultureInfo.InvariantCulture, out var speed) && speed > 0)
                    {
                        var remainingTime = (totalDuration.Value - currentTime) / speed;
                        job.Eta = FormatTime(remainingTime);
                        job.Speed = $"{speed:F2}x";
                    }

                    // Parse FPS
                    if (fpsMatch.Success && double.TryParse(fpsMatch.Groups[1].Value, CultureInfo.InvariantCulture, out var fps))
                    {
                        job.Fps = $"{fps:F1}";
                    }

                    NotifyConversionUpdated(job);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error parsing FFmpeg output: {Output}", output);
        }
    }

    private string FormatTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
        return $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    private void NotifyConversionUpdated(ConversionJob job)
    {
        _db.UpsertConversionJob(job);
        ConversionUpdated?.Invoke(this, job);
    }

    public async Task<string?> GenerateThumbnailAsync(string videoPath, string thumbnailId)
    {
        var thumbnails = await GenerateMultipleThumbnailsAsync(videoPath, thumbnailId);
        return thumbnails?.FirstOrDefault();
    }

    public async Task<List<string>?> GenerateMultipleThumbnailsAsync(string videoPath, string thumbnailId)
    {
        try
        {
            // Get video duration first
            var duration = await GetVideoDurationAsync(videoPath);
            if (!duration.HasValue || duration.Value <= 0)
            {
                _logger.LogWarning("Could not determine video duration for thumbnail generation: {VideoPath}", videoPath);
                return null;
            }

            // Generate thumbnails at 10%, 25%, 40%, 55%, 70%, 85% of video duration
            var percentages = new[] { 0.10, 0.25, 0.40, 0.55, 0.70, 0.85 };
            var generatedThumbnails = new List<string>();

            for (int i = 0; i < percentages.Length; i++)
            {
                var timestamp = duration.Value * percentages[i];
                var thumbnailFileName = $"{thumbnailId}_thumb_{i + 1:D2}.jpg";
                var thumbnailPath = _fileSystem.CombinePaths(_settings.ThumbnailDirectory, thumbnailFileName);

                // Build FFmpeg command to extract a single frame
                var args = $"-ss {timestamp.ToString(CultureInfo.InvariantCulture)} " +
                          $"-i \"{videoPath}\" " +
                          $"-vframes 1 " +
                          $"-vf scale=1280:-1 " +
                          $"-q:v 2 " +
                          $"-y " +
                          $"\"{thumbnailPath}\"";

                _logger.LogInformation("Generating thumbnail {Index}/{Total}: {FfmpegPath} {Args}",
                    i + 1, percentages.Length, _settings.FfmpegPath, args);

                var processInfo = new ProcessStartInfo
                {
                    FileName = _settings.FfmpegPath,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                var process = Process.Start(processInfo);
                if (process == null)
                {
                    _logger.LogError("Failed to start ffmpeg process for thumbnail generation");
                    continue;
                }

                await process.WaitForExitAsync();

                if (process.ExitCode == 0 && _fileSystem.FileExists(thumbnailPath))
                {
                    _logger.LogInformation("Thumbnail generated successfully: {ThumbnailPath}", thumbnailPath);
                    generatedThumbnails.Add(thumbnailFileName);
                }
                else
                {
                    var error = await process.StandardError.ReadToEndAsync();
                    _logger.LogError("Thumbnail generation failed with exit code {ExitCode}. Error: {Error}",
                        process.ExitCode, error);
                }
            }

            return generatedThumbnails.Any() ? generatedThumbnails : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating thumbnails for {VideoPath}", videoPath);
            return null;
        }
    }

    [GeneratedRegex(@"out_time_ms=(\d+)")]
    private static partial Regex TimeMillisecondsRegex();

    [GeneratedRegex(@"speed=\s*(\d+\.?\d*)x")]
    private static partial Regex SpeedRegex();

    [GeneratedRegex(@"fps=\s*(\d+\.?\d*)")]
    private static partial Regex FpsRegex();
}
