using SDL.Models;

namespace SDL.Services.Downloading;
using SDL.Services.Queues;
using SDL.Services.Infrastructure;

public class DownloadBackgroundService : BackgroundService
{
    private readonly IDownloadQueue _queue;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DownloadBackgroundService> _logger;

    public DownloadBackgroundService(
        IDownloadQueue queue,
        IServiceProvider serviceProvider,
        ILogger<DownloadBackgroundService> logger)
    {
        _queue = queue;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Download Background Service is starting.");

        await ResumeInterruptedJobsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var job = await _queue.DequeueDownloadAsync(stoppingToken);
                // Process downloads in parallel
                _ = Task.Run(() => ProcessDownloadJobAsync(job, stoppingToken), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while dequeuing download job.");
            }
        }

        _logger.LogInformation("Download Background Service is stopping.");
    }

    private async Task ResumeInterruptedJobsAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<VideoDatabaseService>();
            
            var interruptedJobs = db.GetActiveDownloadJobs()
                .Where(j => j.Status == DownloadStatus.Starting || 
                            j.Status == DownloadStatus.Downloading || 
                            j.Status == DownloadStatus.Processing)
                .ToList();

            foreach (var job in interruptedJobs)
            {
                _logger.LogInformation("Resuming interrupted download job: {JobId} ({Title})", job.Id, job.Title);
                await _queue.QueueDownloadAsync(job);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during download job resumption.");
        }
    }

    private async Task ProcessDownloadJobAsync(DownloadJob job, CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var downloadService = scope.ServiceProvider.GetRequiredService<StreamDownloadService>();
            await downloadService.ExecuteDownloadAsync(job, stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error processing download job {JobId}", job.Id);
        }
    }
}
