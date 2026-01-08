using SDL.Models;

namespace SDL.Services;

public class ConversionBackgroundService : BackgroundService
{
    private readonly IConversionQueue _queue;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ConversionBackgroundService> _logger;

    public ConversionBackgroundService(
        IConversionQueue queue,
        IServiceProvider serviceProvider,
        ILogger<ConversionBackgroundService> logger)
    {
        _queue = queue;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Conversion Background Service is starting.");

        await ResumeInterruptedJobsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var job = await _queue.DequeueConversionAsync(stoppingToken);
                // Process conversions sequentially to conserve resources
                await ProcessConversionJobAsync(job, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while dequeuing conversion job.");
            }
        }

        _logger.LogInformation("Conversion Background Service is stopping.");
    }

    private async Task ResumeInterruptedJobsAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<VideoDatabaseService>();
            
            var interruptedJobs = db.GetActiveConversionJobs()
                .Where(j => j.Status == ConversionStatus.Queued || 
                            j.Status == ConversionStatus.Converting)
                .ToList();

            foreach (var job in interruptedJobs)
            {
                _logger.LogInformation("Resuming interrupted conversion job: {JobId} ({Title})", job.Id, job.Title);
                await _queue.QueueConversionAsync(job);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during conversion job resumption.");
        }
    }

    private async Task ProcessConversionJobAsync(ConversionJob job, CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var conversionService = scope.ServiceProvider.GetRequiredService<VideoConversionService>();
            await conversionService.ExecuteConversionAsync(job, stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error processing conversion job {JobId}", job.Id);
        }
    }
}
