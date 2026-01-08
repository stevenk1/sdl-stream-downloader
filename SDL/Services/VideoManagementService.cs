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

    public VideoManagementService(
        StreamDownloadService downloadService,
        VideoConversionService conversionService,
        VideoArchiveService archiveService,
        ILogger<VideoManagementService> logger,
        IOptions<VideoStorageSettings> settings)
    {
        _downloadService = downloadService;
        _conversionService = conversionService;
        _archiveService = archiveService;
        _logger = logger;
        _settings = settings.Value;
    }

    /// <summary>
    /// Archives a download job, handling both normal and converted videos.
    /// </summary>
    public async Task ArchiveJobAsync(DownloadJob job)
    {
        try
        {
            // Check if it's a converted job
            bool isConverted = !string.IsNullOrEmpty(job.ConvertedFilePath) && File.Exists(job.ConvertedFilePath);
            string? originalFilePath = job.OutputPath;

            if (isConverted)
            {
                // Verify converted file exists
                if (!File.Exists(job.ConvertedFilePath))
                {
                    throw new FileNotFoundException("Converted file not found", job.ConvertedFilePath);
                }

                // Update the job's OutputPath to point to the converted file for archiving
                job.OutputPath = job.ConvertedFilePath!;
            }

            // Perform the archiving
            await _archiveService.ArchiveVideoAsync(job);

            // If it was a conversion, clean up the original file
            if (isConverted && !string.IsNullOrEmpty(originalFilePath) && File.Exists(originalFilePath))
            {
                try
                {
                    File.Delete(originalFilePath);
                    _logger.LogInformation("Deleted original file after archiving conversion: {FilePath}", originalFilePath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete original file after archiving conversion: {FilePath}", originalFilePath);
                }
            }

            // Clean up the job from active/converted lists
            _downloadService.RemoveDownloadJob(job.Id);

            // Clean up associated conversion job if any
            var conversion = _conversionService.GetConversionByDownloadJobId(job.Id);
            if (conversion != null)
            {
                _conversionService.RemoveConversionJob(conversion.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to archive job {JobId}", job.Id);
            throw;
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
            if (!string.IsNullOrEmpty(job.ConvertedFilePath) && File.Exists(job.ConvertedFilePath))
            {
                try
                {
                    File.Delete(job.ConvertedFilePath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete converted file {FilePath}", job.ConvertedFilePath);
                }
            }

            // Delete original download file if it exists
            if (!string.IsNullOrEmpty(job.OutputPath) && File.Exists(job.OutputPath))
            {
                try
                {
                    File.Delete(job.OutputPath);
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
                    var thumbnailPath = Path.Combine(_settings.ThumbnailDirectory, thumbnailFileName);
                    if (File.Exists(thumbnailPath))
                    {
                        try
                        {
                            File.Delete(thumbnailPath);
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

            // Clean up conversion job if it exists
            var conversion = _conversionService.GetConversionByDownloadJobId(job.Id);
            if (conversion != null)
            {
                _conversionService.RemoveConversionJob(conversion.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete job {JobId}", job.Id);
            throw;
        }
    }
}
