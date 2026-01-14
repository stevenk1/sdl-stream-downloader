using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SDL.Configuration;
using System.Globalization;
using SDL.Models;
using CliWrap;
using CliWrap.EventStream;

namespace SDL.Services.Downloading;
using SDL.Services.Queues;
using SDL.Services.Infrastructure;
using SDL.Services.Conversion;

public partial class StreamDownloadService
{
    private readonly VideoStorageSettings _settings;
    private readonly ILogger<StreamDownloadService> _logger;
    private readonly VideoConversionService _conversionService;
    private readonly VideoDatabaseService _db;
    private readonly IFileSystemService _fileSystem;
    private readonly IDownloadQueue _downloadQueue;
    private readonly Dictionary<string, CancellationTokenSource> _activeDownloads = new();

    public event EventHandler<DownloadJob>? DownloadUpdated;

    public StreamDownloadService(
        IOptions<VideoStorageSettings> settings,
        ILogger<StreamDownloadService> logger,
        VideoDatabaseService db,
        VideoConversionService conversionService,
        IFileSystemService fileSystem,
        IDownloadQueue downloadQueue)
    {
        _settings = settings.Value;
        _logger = logger;
        _db = db;
        _conversionService = conversionService;
        _fileSystem = fileSystem;
        _downloadQueue = downloadQueue;

        // Ensure directories exist
        _fileSystem.CreateDirectory(_settings.DownloadDirectory);
        _fileSystem.CreateDirectory(_settings.ArchiveDirectory);
    }

    public IEnumerable<DownloadJob> GetActiveDownloads() => _db.GetActiveDownloadJobs();

    public IEnumerable<DownloadJob> GetConvertedJobs() => _db.GetConvertedJobs();

    public IEnumerable<DownloadJob> GetAllJobs() => _db.GetAllDownloadJobs();

    public void RemoveDownloadJob(string jobId)
    {
        _db.DeleteDownloadJob(jobId);
        _logger.LogInformation("Removed download job {JobId} from database", jobId);
    }

    public async Task<DownloadJob> StartDownloadAsync(string url, string resolution = "Best")
    {
        var job = new DownloadJob
        {
            Url = url,
            Resolution = resolution,
            Status = DownloadStatus.Starting
        };

        _db.UpsertDownloadJob(job);
        NotifyDownloadUpdated(job);

        await _downloadQueue.QueueDownloadAsync(job);

        return job;
    }

    public async Task<bool> StopDownloadAsync(string jobId)
    {
        if (!_activeDownloads.TryGetValue(jobId, out var cts))
            return false;

        try
        {
            cts.Cancel();
            _activeDownloads.Remove(jobId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping download {JobId}", jobId);
            return false;
        }
    }

    internal async Task ExecuteDownloadAsync(DownloadJob job, CancellationToken stoppingToken)
    {
        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _activeDownloads[job.Id] = jobCts;

        try
        {
            var filename = _settings.DownloadFilenameTemplate.Replace("{id}", job.Id);
            var outputTemplate = _fileSystem.CombinePaths(_settings.DownloadDirectory, filename);

            // Build format string based on resolution selection
            var formatString = job.Resolution.ToLower() switch
            {
                "1080p" => $"bestvideo[height<=1080][ext={_settings.OutputFormat}]/bestvideo[height<=1080]+bestaudio/best[height<=1080]",
                "720p" => $"bestvideo[height<=720][ext={_settings.OutputFormat}]/bestvideo[height<=720]+bestaudio/best[height<=720]",
                "480p" => $"bestvideo[height<=480][ext={_settings.OutputFormat}]/bestvideo[height<=480]+bestaudio/best[height<=480]",
                "360p" => $"bestvideo[height<=360][ext={_settings.OutputFormat}]/bestvideo[height<=360]+bestaudio/best[height<=360]",
                _ => $"best[ext={_settings.OutputFormat}]/bestvideo+bestaudio/best" // Best quality
            };

            var cmd = Cli.Wrap(_settings.YtDlpPath)
                .WithArguments(args => args
                    .Add("--no-playlist")
                    .Add("--format").Add(formatString)
                    .Add("--merge-output-format").Add(_settings.OutputFormat)
                    .Add("--output").Add(outputTemplate)
                    .Add("--print-json")
                    .Add("--progress")
                    .Add("--newline")
                    .Add("--retry-sleep").Add("http:exp=10:120")
                    .Add(job.Url));

            _logger.LogInformation("Starting download for {JobId} using yt-dlp: {Command}", job.Id, cmd.ToString());
            job.Status = DownloadStatus.Downloading;
            NotifyDownloadUpdated(job);

            int exitCode = 0;
            bool cancelled = false;

            try
            {
                await foreach (var cmdEvent in cmd.ListenAsync(jobCts.Token))
                {
                    switch (cmdEvent)
                    {
                        case StandardOutputCommandEvent stdOut:
                            ParseYtDlpOutput(job, stdOut.Text);
                            break;
                        case StandardErrorCommandEvent stdErr:
                            _logger.LogWarning("yt-dlp stderr: {Data}", stdErr.Text);
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
                job.Status = DownloadStatus.Stopped;
            }

            // Search for output files for stopped or completed downloads
            string? outputFile = null;

            // First check for .part files (incomplete downloads)
            var partFiles = _fileSystem.GetFiles(_settings.DownloadDirectory, $"{job.Id}.*.part");
            if (partFiles.Length > 0)
            {
                outputFile = partFiles[0];
            }
            else
            {
                // Check for completed files
                var files = _fileSystem.GetFiles(_settings.DownloadDirectory, $"{job.Id}.*");
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
            if (cancelled || exitCode == 137 || exitCode == 143)
            {
                job.Status = DownloadStatus.Stopped;
            }
            else if (exitCode == 0)
            {
                job.Status = DownloadStatus.Completed;
                job.Progress = 100;
            }
            else
            {
                job.Status = DownloadStatus.Failed;
                job.ErrorMessage = "Download failed with exit code " + exitCode;
            }

            NotifyDownloadUpdated(job);

            // Auto-trigger conversion for completed or stopped downloads
            if ((job.Status == DownloadStatus.Completed || job.Status == DownloadStatus.Stopped) &&
                !string.IsNullOrEmpty(outputFile) && _fileSystem.FileExists(outputFile))
            {
                try
                {
                    if (IsFormatWebPlayable(outputFile))
                    {
                        _logger.LogInformation("Skipping conversion for {JobId} as it is already in a web-playable format: {FilePath}", job.Id, outputFile);
                        
                        // We still need thumbnails for the UI
                        var thumbnails = await _conversionService.GenerateMultipleThumbnailsAsync(outputFile, job.Id);
                        
                        job.Status = DownloadStatus.ConversionCompleted;
                        job.ConvertedFilePath = outputFile;
                        if (thumbnails != null && thumbnails.Any())
                        {
                            job.Thumbnails = thumbnails;
                            job.Thumbnail = thumbnails.First();
                        }
                        NotifyDownloadUpdated(job);
                    }
                    else
                    {
                        job.Status = DownloadStatus.Converting;
                        NotifyDownloadUpdated(job);
                        await _conversionService.StartConversionAsync(job);
                    }
                }
                catch (Exception convEx)
                {
                    _logger.LogError(convEx, "Error processing/starting conversion for job {JobId}", job.Id);
                    job.Status = DownloadStatus.ConversionFailed;
                    job.ErrorMessage = $"Processing failed: {convEx.Message}";
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
        }
        finally
        {
            _activeDownloads.Remove(job.Id);
        }
    }

    private bool IsFormatWebPlayable(string filePath)
    {
        var extension = _fileSystem.GetExtension(filePath).ToLowerInvariant();
        return extension switch
        {
            ".mp4" or ".webm" or ".mp3" or ".m4a" or ".aac" or ".ogg" or ".opus" or ".wav" => true,
            _ => false
        };
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

    public void NotifyDownloadUpdated(DownloadJob job)
    {
        _db.UpsertDownloadJob(job);
        DownloadUpdated?.Invoke(this, job);
    }

    [GeneratedRegex(@"\[download\]\s+(\d+\.?\d*)%.*?(?:at\s+([^\s]+))?\s+(?:ETA\s+(.+))?")]
    private static partial Regex ProgressRegex();
}
