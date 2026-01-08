using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SDL.Configuration;
using System.Globalization;
using SDL.Models;
using CliWrap;
using CliWrap.EventStream;
using CliWrap.Buffered;

namespace SDL.Services;

public partial class VideoConversionService
{
    private readonly VideoStorageSettings _settings;
    private readonly ILogger<VideoConversionService> _logger;
    private readonly VideoDatabaseService _db;
    private readonly IFileSystemService _fileSystem;
    private readonly IConversionQueue _conversionQueue;
    private readonly Dictionary<string, CancellationTokenSource> _activeConversions = new();

    public event EventHandler<ConversionJob>? ConversionUpdated;

    public VideoConversionService(
        IOptions<VideoStorageSettings> settings, 
        ILogger<VideoConversionService> logger, 
        VideoDatabaseService db, 
        IFileSystemService fileSystem,
        IConversionQueue conversionQueue)
    {
        _settings = settings.Value;
        _logger = logger;
        _db = db;
        _fileSystem = fileSystem;
        _conversionQueue = conversionQueue;

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
        var convertedFileName = _settings.ConvertedFilenameTemplate
            .Replace("{fn}", fileName)
            .Replace("{ext}", _settings.ConversionOutputFormat);
            
        job.OutputPath = _fileSystem.CombinePaths(_settings.ConvertedDirectory, convertedFileName);

        _db.UpsertConversionJob(job);
        NotifyConversionUpdated(job);

        await _conversionQueue.QueueConversionAsync(job);

        return job;
    }

    public async Task<bool> CancelConversionAsync(string jobId)
    {
        if (!_activeConversions.TryGetValue(jobId, out var cts))
            return false;

        try
        {
            cts.Cancel();
            _activeConversions.Remove(jobId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error canceling conversion {JobId}", jobId);
            return false;
        }
    }

    internal async Task ExecuteConversionAsync(ConversionJob job, CancellationToken stoppingToken)
    {
        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _activeConversions[job.Id] = jobCts;

        try
        {
            // Build FFmpeg command
            var cmd = Cli.Wrap(_settings.FfmpegPath)
                .WithArguments(args => args
                    .Add("-i").Add(job.SourcePath)
                    .Add("-c:v").Add(_settings.VideoCodec)
                    .Add("-c:a").Add(_settings.AudioCodec)
                    .Add("-b:a").Add(_settings.AudioBitrate)
                    .Add("-progress").Add("pipe:1")
                    .Add("-stats_period").Add("0.5")
                    .Add("-y")
                    .Add(job.OutputPath));

            _logger.LogInformation("Executing FFmpeg command: {Command}", cmd.ToString());

            // Get duration from ffprobe first for accurate progress
            double? totalDuration = await GetVideoDurationAsync(job.SourcePath);

            job.Status = ConversionStatus.Converting;
            NotifyConversionUpdated(job);

            int exitCode = 0;
            bool cancelled = false;

            try
            {
                await foreach (var cmdEvent in cmd.ListenAsync(jobCts.Token))
                {
                    switch (cmdEvent)
                    {
                        case StandardOutputCommandEvent stdOut:
                            ParseFfmpegProgress(job, stdOut.Text, totalDuration);
                            break;
                        case StandardErrorCommandEvent stdErr:
                            // FFmpeg outputs some progress/info to stderr too
                            ParseFfmpegProgress(job, stdErr.Text, totalDuration);
                            break;
                        case ExitedCommandEvent exited:
                            exitCode = exited.ExitCode;
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }

            // Determine final status
            if (cancelled || exitCode == 137 || exitCode == 143 || exitCode == 255)
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
            else if (exitCode == 0 && _fileSystem.FileExists(job.OutputPath))
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
                job.ErrorMessage = $"Conversion failed with exit code {exitCode}";

                // Update the corresponding DownloadJob status
                UpdateDownloadJobAfterConversion(job.DownloadJobId, DownloadStatus.ConversionFailed, job.ErrorMessage, null, null);
            }

            NotifyConversionUpdated(job);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing conversion for job {JobId}", job.Id);
            job.Status = ConversionStatus.Failed;
            job.ErrorMessage = ex.Message;

            // Update the corresponding DownloadJob status
            UpdateDownloadJobAfterConversion(job.DownloadJobId, DownloadStatus.ConversionFailed, ex.Message, null, null);

            NotifyConversionUpdated(job);
        }
        finally
        {
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
            var result = await Cli.Wrap("ffprobe")
                .WithArguments(args => args
                    .Add("-v").Add("error")
                    .Add("-show_entries").Add("format=duration")
                    .Add("-of").Add("default=noprint_wrappers=1:nokey=1")
                    .Add(videoPath))
                .ExecuteBufferedAsync();

            if (double.TryParse(result.StandardOutput.Trim(), CultureInfo.InvariantCulture, out var duration))
            {
                _logger.LogInformation("Video duration for {VideoPath}: {Duration} seconds", videoPath, duration);
                return duration;
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
                var thumbnailFileName = _settings.ThumbnailFilenameTemplate
                    .Replace("{id}", thumbnailId)
                    .Replace("{index:D2}", (i + 1).ToString("D2"));
                
                var thumbnailPath = _fileSystem.CombinePaths(_settings.ThumbnailDirectory, thumbnailFileName);

                _logger.LogInformation("Generating thumbnail {Index}/{Total}: {FfmpegPath} at {Timestamp}s",
                    i + 1, percentages.Length, _settings.FfmpegPath, timestamp);

                var result = await Cli.Wrap(_settings.FfmpegPath)
                    .WithArguments(args => args
                        .Add("-ss").Add(timestamp.ToString(CultureInfo.InvariantCulture))
                        .Add("-i").Add(videoPath)
                        .Add("-vframes").Add("1")
                        .Add("-vf").Add("scale=1280:-1")
                        .Add("-q:v").Add("2")
                        .Add("-y")
                        .Add(thumbnailPath))
                    .ExecuteAsync();

                if (result.ExitCode == 0 && _fileSystem.FileExists(thumbnailPath))
                {
                    _logger.LogInformation("Thumbnail generated successfully: {ThumbnailPath}", thumbnailPath);
                    generatedThumbnails.Add(thumbnailFileName);
                }
                else
                {
                    _logger.LogError("Thumbnail generation failed with exit code {ExitCode}", result.ExitCode);
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
