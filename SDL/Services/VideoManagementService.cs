using Microsoft.Extensions.Options;
using SDL.Configuration;
using SDL.Models;

namespace SDL.Services;

public class VideoManagementService
{
    private readonly StreamDownloadService _downloadService;
    private readonly VideoConversionService _conversionService;
    private readonly VideoArchiveService _archiveService;
    private readonly ILogger<VideoManagementService> _logger;
    private readonly VideoStorageSettings _settings;
    private readonly IFileSystemService _fileSystem;
    private readonly IArchiveQueue _archiveQueue;

    public VideoManagementService(
        StreamDownloadService downloadService,
        VideoConversionService conversionService,
        VideoArchiveService archiveService,
        ILogger<VideoManagementService> logger,
        IOptions<VideoStorageSettings> settings,
        IFileSystemService fileSystem,
        IArchiveQueue archiveQueue)
    {
        _downloadService = downloadService;
        _conversionService = conversionService;
        _archiveService = archiveService;
        _logger = logger;
        _settings = settings.Value;
        _fileSystem = fileSystem;
        _archiveQueue = archiveQueue;
    }

    /// <summary>
    /// Archives a download job, handling both normal and converted videos.
    /// This queues the job for background archiving.
    /// </summary>
    public async Task ArchiveJobAsync(DownloadJob job)
    {
        job.Status = DownloadStatus.Archiving;
        job.Progress = 0;
        _downloadService.NotifyDownloadUpdated(job);
        
        await _archiveQueue.QueueArchiveAsync(job);
        _logger.LogInformation("Queued job {JobId} for archiving", job.Id);
    }

    /// <summary>
    /// Executes the actual archiving process.
    /// </summary>
    public async Task ExecuteArchiveAsync(DownloadJob job)
    {
        try
        {
            // Check if it's a converted job
            bool isConverted = !string.IsNullOrEmpty(job.ConvertedFilePath) && _fileSystem.FileExists(job.ConvertedFilePath);
            string fileToArchive = job.OutputPath;

            if (isConverted)
            {
                // Verify converted file exists
                if (!_fileSystem.FileExists(job.ConvertedFilePath))
                {
                    throw new FileNotFoundException("Converted file not found", job.ConvertedFilePath);
                }

                // Use the converted file for archiving
                fileToArchive = job.ConvertedFilePath!;
            }

            // Perform the archiving
            await _archiveService.ArchiveVideoAsync(job, fileToArchive, j => _downloadService.NotifyDownloadUpdated(j));

            // If it was a conversion, clean up the original file
            if (isConverted && !string.IsNullOrEmpty(job.OutputPath) && _fileSystem.FileExists(job.OutputPath))
            {
                try
                {
                    _fileSystem.FileDelete(job.OutputPath);
                    _logger.LogInformation("Deleted original file after archiving conversion: {FilePath}", job.OutputPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete original file after archiving conversion: {FilePath}", job.OutputPath);
                }
            }

            // Clean up the job from active/converted lists
            _downloadService.RemoveDownloadJob(job.Id);
            job.Status = DownloadStatus.Archived;
            _downloadService.NotifyDownloadUpdated(job);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to archive job {JobId}", job.Id);
            job.Status = DownloadStatus.ArchivingFailed;
            job.ErrorMessage = ex.Message;
            _downloadService.NotifyDownloadUpdated(job);
        }
    }

    /// <summary>
    /// Deletes a download job and all associated files (original, converted, thumbnails).
    /// </summary>
    public async Task DeleteJobAsync(DownloadJob job)
    {
        try
        {
            // Delete the converted file if it exists
            if (!string.IsNullOrEmpty(job.ConvertedFilePath) && _fileSystem.FileExists(job.ConvertedFilePath))
            {
                try
                {
                    _fileSystem.FileDelete(job.ConvertedFilePath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete converted file {FilePath}", job.ConvertedFilePath);
                }
            }

            // Delete original download file if it exists
            if (!string.IsNullOrEmpty(job.OutputPath) && _fileSystem.FileExists(job.OutputPath))
            {
                try
                {
                    _fileSystem.FileDelete(job.OutputPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete original file {FilePath}", job.OutputPath);
                }
            }

            // Delete thumbnails if they exist
            if (job.Thumbnails != null)
            {
                foreach (var thumbnailFileName in job.Thumbnails)
                {
                    var thumbnailPath = _fileSystem.CombinePaths(_settings.ThumbnailDirectory, thumbnailFileName);
                    if (_fileSystem.FileExists(thumbnailPath))
                    {
                        try
                        {
                            _fileSystem.FileDelete(thumbnailPath);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to delete thumbnail {ThumbnailPath}", thumbnailPath);
                        }
                    }
                }
            }

            // Remove from database
            _downloadService.RemoveDownloadJob(job.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete job {JobId}", job.Id);
            throw;
        }
    }
}
