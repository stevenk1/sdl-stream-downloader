using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SDL.Configuration;
using System.Globalization;
using SDL.Models;

namespace SDL.Services;

public partial class StreamDownloadService
{
    private readonly VideoStorageSettings _settings;
    private readonly ILogger<StreamDownloadService> _logger;
    private readonly VideoConversionService _conversionService;
    private readonly Dictionary<string, (Process Process, CancellationTokenSource Cts)> _activeDownloads = new();
    private readonly Dictionary<string, DownloadJob> _downloadJobs = new();

    public event EventHandler<DownloadJob>? DownloadUpdated;

    public StreamDownloadService(
        IOptions<VideoStorageSettings> settings,
        ILogger<StreamDownloadService> logger,
        VideoConversionService conversionService)
    {
        _settings = settings.Value;
        _logger = logger;
        _conversionService = conversionService;

        // Ensure directories exist
        Directory.CreateDirectory(_settings.DownloadDirectory);
        Directory.CreateDirectory(_settings.ArchiveDirectory);
    }

    public IEnumerable<DownloadJob> GetActiveDownloads() => _downloadJobs.Values;

    public async Task<DownloadJob> StartDownloadAsync(string url, string resolution = "Best")
    {
        var job = new DownloadJob
        {
            Url = url,
            Resolution = resolution,
            Status = DownloadStatus.Starting
        };

        _downloadJobs[job.Id] = job;
        NotifyDownloadUpdated(job);

        var cts = new CancellationTokenSource();
        _ = Task.Run(() => ExecuteDownloadAsync(job, cts.Token), cts.Token);

        return job;
    }

    public async Task<bool> StopDownloadAsync(string jobId)
    {
        if (!_activeDownloads.TryGetValue(jobId, out var download))
            return false;

        try
        {
            download.Cts.Cancel();

            if (!download.Process.HasExited)
            {
                download.Process.Kill(entireProcessTree: true);
                await download.Process.WaitForExitAsync();
            }

            // Note: Status will be set to Stopped by ExecuteDownloadAsync when it detects
            // the cancellation or exit code 137/143. Don't set it here to avoid race condition.

            _activeDownloads.Remove(jobId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping download {JobId}", jobId);
            return false;
        }
    }

    private async Task ExecuteDownloadAsync(DownloadJob job, CancellationToken cancellationToken)
    {
        try
        {
            var outputTemplate = Path.Combine(_settings.DownloadDirectory, $"{job.Id}.%(ext)s");

            // Build format string based on resolution selection
            string formatString = job.Resolution.ToLower() switch
            {
                "1080p" => $"bestvideo[height<=1080][ext={_settings.OutputFormat}]/bestvideo[height<=1080]+bestaudio/best[height<=1080]",
                "720p" => $"bestvideo[height<=720][ext={_settings.OutputFormat}]/bestvideo[height<=720]+bestaudio/best[height<=720]",
                "480p" => $"bestvideo[height<=480][ext={_settings.OutputFormat}]/bestvideo[height<=480]+bestaudio/best[height<=480]",
                "360p" => $"bestvideo[height<=360][ext={_settings.OutputFormat}]/bestvideo[height<=360]+bestaudio/best[height<=360]",
                _ => $"best[ext={_settings.OutputFormat}]/bestvideo+bestaudio/best" // Best quality
            };

            var args = $"--no-playlist --format \"{formatString}\" " +
                      $"--merge-output-format {_settings.OutputFormat} " +
                      $"--output \"{outputTemplate}\" " +
                      $"--print-json " +
                      $"--progress " +
                      $"--newline " +
                      $"\"{job.Url}\"";

            var processInfo = new ProcessStartInfo
            {
                FileName = _settings.YtDlpPath,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var process = new Process { StartInfo = processInfo };
            var cts = new CancellationTokenSource();
            _activeDownloads[job.Id] = (process, cts);

            process.OutputDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    ParseYtDlpOutput(job, e.Data);
                }
            };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    _logger.LogWarning("yt-dlp stderr: {Data}", e.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            job.Status = DownloadStatus.Downloading;
            NotifyDownloadUpdated(job);

            await process.WaitForExitAsync(cancellationToken);

            // Search for output files for stopped or completed downloads
            string? outputFile = null;

            // First check for .part files (incomplete downloads)
            var partFiles = Directory.GetFiles(_settings.DownloadDirectory, $"{job.Id}.*.part");
            if (partFiles.Length > 0)
            {
                outputFile = partFiles[0];
            }
            else
            {
                // Check for completed files
                var files = Directory.GetFiles(_settings.DownloadDirectory, $"{job.Id}.*");
                if (files.Length > 0)
                {
                    outputFile = files[0];
                }
            }

            if (outputFile != null)
            {
                job.OutputPath = outputFile;
            }

            // Determine final status based on cancellation and exit code
            // Exit codes 137 (SIGKILL) and 143 (SIGTERM) indicate manual termination
            if (cancellationToken.IsCancellationRequested || process.ExitCode == 137 || process.ExitCode == 143)
            {
                job.Status = DownloadStatus.Stopped;
            }
            else if (process.ExitCode == 0)
            {
                job.Status = DownloadStatus.Completed;
                job.Progress = 100;
            }
            else
            {
                job.Status = DownloadStatus.Failed;
                job.ErrorMessage = "Download failed with exit code " + process.ExitCode;
            }

            NotifyDownloadUpdated(job);
            _activeDownloads.Remove(job.Id);

            // Auto-trigger conversion for completed or stopped downloads
            if ((job.Status == DownloadStatus.Completed || job.Status == DownloadStatus.Stopped) &&
                !string.IsNullOrEmpty(outputFile) && File.Exists(outputFile))
            {
                try
                {
                    job.Status = DownloadStatus.Converting;
                    NotifyDownloadUpdated(job);
                    await _conversionService.StartConversionAsync(job);
                }
                catch (Exception convEx)
                {
                    _logger.LogError(convEx, "Error starting conversion for job {JobId}", job.Id);
                    job.Status = DownloadStatus.ConversionFailed;
                    job.ErrorMessage = $"Conversion failed to start: {convEx.Message}";
                    NotifyDownloadUpdated(job);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing download for job {JobId}", job.Id);
            job.Status = DownloadStatus.Failed;
            job.ErrorMessage = ex.Message;
            NotifyDownloadUpdated(job);
            _activeDownloads.Remove(job.Id);
        }
    }

    private void ParseYtDlpOutput(DownloadJob job, string output)
    {
        try
        {
            // Parse JSON metadata
            if (output.StartsWith("{") && output.EndsWith("}"))
            {
                try
                {
                    var json = System.Text.Json.JsonDocument.Parse(output);
                    if (json.RootElement.TryGetProperty("title", out var title))
                    {
                        job.Title = title.GetString() ?? "Unknown";
                    }
                }
                catch
                {
                    // Ignore JSON parse errors
                }
            }

            // Parse progress: [download]  45.2% of 123.45MiB at 1.23MiB/s ETA 00:45
            var progressMatch = ProgressRegex().Match(output);
            if (progressMatch.Success)
            {
                if (double.TryParse(progressMatch.Groups[1].Value, CultureInfo.InvariantCulture, out var progress))
                {
                    job.Progress = progress;
                    job.Speed = progressMatch.Groups[2].Value;
                    job.Eta = progressMatch.Groups[3].Value;
                    NotifyDownloadUpdated(job);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error parsing yt-dlp output: {Output}", output);
        }
    }

    private void NotifyDownloadUpdated(DownloadJob job)
    {
        DownloadUpdated?.Invoke(this, job);
    }

    [GeneratedRegex(@"\[download\]\s+(\d+\.?\d*)%.*?(?:at\s+([^\s]+))?\s+(?:ETA\s+(.+))?")]
    private static partial Regex ProgressRegex();
}
