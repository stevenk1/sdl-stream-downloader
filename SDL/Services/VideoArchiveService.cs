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
    private List<ArchivedVideo> _videos = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public event EventHandler? ArchiveUpdated;

    public VideoArchiveService(IOptions<VideoStorageSettings> settings, ILogger<VideoArchiveService> logger, VideoConversionService conversionService)
    {
        _settings = settings.Value;
        _logger = logger;
        _conversionService = conversionService;
        LoadMetadata();
    }

    public async Task<IEnumerable<ArchivedVideo>> GetArchivedVideosAsync()
    {
        await _lock.WaitAsync();
        try
        {
            return _videos.ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ArchivedVideo> ArchiveVideoAsync(DownloadJob completedJob)
    {
        await _lock.WaitAsync();
        try
        {
            if (string.IsNullOrEmpty(completedJob.OutputPath) || !File.Exists(completedJob.OutputPath))
            {
                throw new FileNotFoundException("Download file not found", completedJob.OutputPath);
            }

            // Move file to archive directory
            var fileName = Path.GetFileName(completedJob.OutputPath);

            // Remove .part extension for stopped downloads
            if (fileName.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            {
                fileName = fileName.Substring(0, fileName.Length - 5);
            }

            var archivePath = Path.Combine(_settings.ArchiveDirectory, fileName);

            // If file already exists in archive, generate unique name
            if (File.Exists(archivePath))
            {
                var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
                var extension = Path.GetExtension(fileName);
                fileName = $"{nameWithoutExt}_{DateTime.Now:yyyyMMddHHmmss}{extension}";
                archivePath = Path.Combine(_settings.ArchiveDirectory, fileName);
            }

            File.Move(completedJob.OutputPath, archivePath);

            var fileInfo = new FileInfo(archivePath);

            var video = new ArchivedVideo
            {
                Title = completedJob.Title,
                OriginalUrl = completedJob.Url,
                FilePath = archivePath,
                FileName = fileName,
                FileSizeBytes = fileInfo.Length,
                ArchivedAt = DateTime.Now
            };

            // Generate thumbnail for the archived video
            try
            {
                var thumbnailFileName = await _conversionService.GenerateThumbnailAsync(archivePath, video.Id);
                if (thumbnailFileName != null)
                {
                    video.Thumbnail = thumbnailFileName;
                    _logger.LogInformation("Thumbnail generated for archived video {VideoId}: {ThumbnailFileName}",
                        video.Id, thumbnailFileName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate thumbnail for archived video {VideoId}", video.Id);
                // Don't fail the archiving if thumbnail generation fails
            }

            _videos.Add(video);
            await SaveMetadataAsync();

            NotifyArchiveUpdated();
            return video;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> DeleteVideoAsync(string videoId)
    {
        await _lock.WaitAsync();
        try
        {
            var video = _videos.FirstOrDefault(v => v.Id == videoId);
            if (video == null)
                return false;

            // Delete file
            if (File.Exists(video.FilePath))
            {
                File.Delete(video.FilePath);
            }

            // Delete thumbnail if it exists
            if (!string.IsNullOrEmpty(video.Thumbnail))
            {
                var thumbnailPath = Path.Combine(_settings.ThumbnailDirectory, video.Thumbnail);
                if (File.Exists(thumbnailPath))
                {
                    try
                    {
                        File.Delete(thumbnailPath);
                        _logger.LogInformation("Deleted thumbnail for video {VideoId}: {ThumbnailFileName}",
                            video.Id, video.Thumbnail);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to delete thumbnail {ThumbnailPath}", thumbnailPath);
                    }
                }
            }

            // Remove from list
            _videos.Remove(video);
            await SaveMetadataAsync();

            NotifyArchiveUpdated();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting video {VideoId}", videoId);
            return false;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ArchivedVideo?> GetVideoAsync(string videoId)
    {
        await _lock.WaitAsync();
        try
        {
            return _videos.FirstOrDefault(v => v.Id == videoId);
        }
        finally
        {
            _lock.Release();
        }
    }

    private void LoadMetadata()
    {
        try
        {
            var metadataPath = GetMetadataPath();
            if (File.Exists(metadataPath))
            {
                var json = File.ReadAllText(metadataPath);
                _videos = JsonSerializer.Deserialize<List<ArchivedVideo>>(json) ?? new List<ArchivedVideo>();

                // Verify files still exist
                _videos = _videos.Where(v => File.Exists(v.FilePath)).ToList();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading video metadata");
            _videos = new List<ArchivedVideo>();
        }
    }

    private async Task SaveMetadataAsync()
    {
        try
        {
            var metadataPath = GetMetadataPath();
            var json = JsonSerializer.Serialize(_videos, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            await File.WriteAllTextAsync(metadataPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving video metadata");
        }
    }

    private string GetMetadataPath()
    {
        return Path.Combine(_settings.ArchiveDirectory, _settings.MetadataFile);
    }

    private void NotifyArchiveUpdated()
    {
        ArchiveUpdated?.Invoke(this, EventArgs.Empty);
    }
}
