using System.Text.Json;
using Microsoft.Extensions.Options;
using SDL.Configuration;
using SDL.Models;

namespace SDL.Services;

public class VideoArchiveService
{
    private readonly VideoStorageSettings _settings;
    private readonly ILogger<VideoArchiveService> _logger;
    private readonly VideoConversionService _conversionService;
    private readonly VideoDatabaseService _db;
    private readonly IFileSystemService _fileSystem;

    public event EventHandler? ArchiveUpdated;

    public VideoArchiveService(IOptions<VideoStorageSettings> settings, ILogger<VideoArchiveService> logger, VideoConversionService conversionService, VideoDatabaseService db, IFileSystemService fileSystem)
    {
        _settings = settings.Value;
        _logger = logger;
        _conversionService = conversionService;
        _db = db;
        _fileSystem = fileSystem;
    }

    public Task<IEnumerable<ArchivedVideo>> GetArchivedVideosAsync()
    {
        return Task.FromResult(_db.GetAllArchivedVideos());
    }

    public Task<(IEnumerable<ArchivedVideo> Items, int TotalCount)> GetArchivedVideosFilteredAsync(int skip, int limit, string? sortBy, bool descending, string? search)
    {
        return Task.FromResult(_db.GetArchivedVideosFiltered(skip, limit, sortBy, descending, search));
    }

    public Task<int> GetArchivedCountAsync()
    {
        return Task.FromResult(_db.GetArchivedVideoCount());
    }

    public async Task<ArchivedVideo> ArchiveVideoAsync(DownloadJob completedJob, string filePath, Action<DownloadJob>? onProgress = null)
    {
        completedJob.Status = DownloadStatus.Archiving;
        completedJob.Progress = 0;
        onProgress?.Invoke(completedJob);

        if (string.IsNullOrEmpty(filePath) || !_fileSystem.FileExists(filePath))
        {
            throw new FileNotFoundException("Download file not found", filePath);
        }

        // Move file to archive directory
        var fileName = _fileSystem.GetFileName(filePath);

        // Remove .part extension for stopped downloads
        if (fileName != null && fileName.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
        {
            fileName = fileName.Substring(0, fileName.Length - 5);
        }

        var archivePath = _fileSystem.CombinePaths(_settings.ArchiveDirectory, fileName!);

        // If file already exists in archive, generate unique name
        if (_fileSystem.FileExists(archivePath))
        {
            var nameWithoutExt = _fileSystem.GetFileNameWithoutExtension(fileName);
            var extension = _fileSystem.GetExtension(fileName!);
            fileName = $"{nameWithoutExt}_{DateTime.Now:yyyyMMddHHmmss}{extension}";
            archivePath = _fileSystem.CombinePaths(_settings.ArchiveDirectory, fileName);
        }

        completedJob.Progress = 10;
        onProgress?.Invoke(completedJob);

        _fileSystem.FileMove(filePath, archivePath);

        completedJob.Progress = 20;
        onProgress?.Invoke(completedJob);

        var fileLength = _fileSystem.GetFileLength(archivePath);

        var video = new ArchivedVideo
        {
            Title = completedJob.Title,
            OriginalUrl = completedJob.Url,
            FilePath = archivePath,
            FileName = fileName!,
            FileSizeBytes = fileLength,
            ArchivedAt = DateTime.Now
        };

        // Generate thumbnails for the archived video
        try
        {
            // We'll update progress for each thumbnail (there are usually 6)
            // Progress will go from 20% to 90%
            var duration = await _conversionService.GetVideoDurationAsync(archivePath);
            if (duration.HasValue && duration.Value > 0)
            {
                var percentages = new[] { 0.10, 0.25, 0.40, 0.55, 0.70, 0.85 };
                var generatedThumbnails = new List<string>();

                for (int i = 0; i < percentages.Length; i++)
                {
                    var timestamp = duration.Value * percentages[i];
                    var thumbnailFileName = _settings.ThumbnailFilenameTemplate
                        .Replace("{id}", video.Id)
                        .Replace("{index:D2}", (i + 1).ToString("D2"));
                    
                    var thumbnailPath = _fileSystem.CombinePaths(_settings.ThumbnailDirectory, thumbnailFileName);

                    _logger.LogInformation("Generating thumbnail {Index}/{Total} during archive: {FfmpegPath} at {Timestamp}s",
                        i + 1, percentages.Length, _settings.FfmpegPath, timestamp);

                    // We need a way to generate a single thumbnail without the full loop in VideoConversionService
                    // Or we can just use the loop if we want to keep it simple, but then we don't get fine-grained progress.
                    // Let's add GenerateSingleThumbnailAsync to VideoConversionService.
                    
                    var thumbnailName = await _conversionService.GenerateSingleThumbnailAsync(archivePath, video.Id, i, timestamp);
                    if (thumbnailName != null)
                    {
                        generatedThumbnails.Add(thumbnailName);
                    }

                    completedJob.Progress = 20 + ((i + 1) * 70 / percentages.Length);
                    onProgress?.Invoke(completedJob);
                }

                if (generatedThumbnails.Any())
                {
                    video.Thumbnails = generatedThumbnails;
                    video.Thumbnail = generatedThumbnails.First();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate thumbnails for archived video {VideoId}", video.Id);
            // Don't fail the archiving if thumbnail generation fails
        }

        completedJob.Progress = 100;
        onProgress?.Invoke(completedJob);

        _db.UpsertArchivedVideo(video);
        NotifyArchiveUpdated();
        return video;
    }

    public Task<bool> DeleteVideoAsync(string videoId)
    {
        try
        {
            var video = _db.GetArchivedVideo(videoId);
            if (video == null)
                return Task.FromResult(false);

            // Delete file
            if (_fileSystem.FileExists(video.FilePath))
            {
                _fileSystem.FileDelete(video.FilePath);
            }

            // Delete all thumbnails if they exist
            var thumbnailsToDelete = video.Thumbnails?.Any() == true
                ? video.Thumbnails
                : (!string.IsNullOrEmpty(video.Thumbnail) ? new List<string> { video.Thumbnail } : new List<string>());

            foreach (var thumbnailFileName in thumbnailsToDelete)
            {
                var thumbnailPath = _fileSystem.CombinePaths(_settings.ThumbnailDirectory, thumbnailFileName);
                if (_fileSystem.FileExists(thumbnailPath))
                {
                    try
                    {
                        _fileSystem.FileDelete(thumbnailPath);
                        _logger.LogInformation("Deleted thumbnail for video {VideoId}: {ThumbnailFileName}",
                            video.Id, thumbnailFileName);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to delete thumbnail {ThumbnailPath}", thumbnailPath);
                    }
                }
            }

            // Remove from database
            _db.DeleteArchivedVideo(videoId);
            NotifyArchiveUpdated();
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting video {VideoId}", videoId);
            return Task.FromResult(false);
        }
    }

    public Task<ArchivedVideo?> GetVideoAsync(string videoId)
    {
        return Task.FromResult(_db.GetArchivedVideo(videoId));
    }

    private void NotifyArchiveUpdated()
    {
        ArchiveUpdated?.Invoke(this, EventArgs.Empty);
    }
}
