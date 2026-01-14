using SDL.Models;

namespace SDL.Services.Archiving;
using SDL.Services.Queues;
using SDL.Services.Downloading;
using SDL.Services.Infrastructure;

public class ArchiveBackgroundService : BackgroundService
{
    private readonly IArchiveQueue _queue;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ArchiveBackgroundService> _logger;

    public ArchiveBackgroundService(
        IArchiveQueue queue,
        IServiceProvider serviceProvider,
        ILogger<ArchiveBackgroundService> logger)
    {
        _queue = queue;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Archive Background Service is starting.");

        await ResumeInterruptedJobsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var job = await _queue.DequeueArchiveAsync(stoppingToken);
                await ProcessArchiveJobAsync(job, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while dequeuing archive job.");
            }
        }

        _logger.LogInformation("Archive Background Service is stopping.");
    }

    private async Task ResumeInterruptedJobsAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var downloadService = scope.ServiceProvider.GetRequiredService<StreamDownloadService>();
            
            var interruptedJobs = downloadService.GetAllJobs()
                .Where(j => j.Status == DownloadStatus.Archiving)
                .ToList();

            foreach (var job in interruptedJobs)
            {
                _logger.LogInformation("Resuming interrupted archive job: {JobId} ({Title})", job.Id, job.Title);
                await _queue.QueueArchiveAsync(job);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during archive job resumption.");
        }
    }

    private async Task ProcessArchiveJobAsync(DownloadJob job, CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var videoManagementService = scope.ServiceProvider.GetRequiredService<VideoManagementService>();
            await videoManagementService.ExecuteArchiveAsync(job);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error processing archive job {JobId}", job.Id);
        }
    }
}
