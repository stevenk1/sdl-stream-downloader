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

    public async Task<ArchivedVideo> ArchiveVideoAsync(DownloadJob completedJob)
    {
        if (string.IsNullOrEmpty(completedJob.OutputPath) || !_fileSystem.FileExists(completedJob.OutputPath))
        {
            throw new FileNotFoundException("Download file not found", completedJob.OutputPath);
        }

        // Move file to archive directory
        var fileName = _fileSystem.GetFileName(completedJob.OutputPath);

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

        _fileSystem.FileMove(completedJob.OutputPath, archivePath);

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
            var thumbnailFileNames = await _conversionService.GenerateMultipleThumbnailsAsync(archivePath, video.Id);
            if (thumbnailFileNames != null && thumbnailFileNames.Any())
            {
                video.Thumbnails = thumbnailFileNames;
                video.Thumbnail = thumbnailFileNames.First(); // Backward compatibility
                _logger.LogInformation("Generated {Count} thumbnails for archived video {VideoId}",
                    thumbnailFileNames.Count, video.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate thumbnails for archived video {VideoId}", video.Id);
            // Don't fail the archiving if thumbnail generation fails
        }

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
