using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SDL.Models;
using SDL.Services.Infrastructure;

namespace SDL.Services.Downloading;

public class SubscriptionBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SubscriptionBackgroundService> _logger;

    public SubscriptionBackgroundService(
        IServiceProvider serviceProvider,
        ILogger<SubscriptionBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Subscription Background Service is starting.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckSubscriptionsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred while checking subscriptions.");
            }

            // Wait for 1 minute before checking again
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }

        _logger.LogInformation("Subscription Background Service is stopping.");
    }

    private async Task CheckSubscriptionsAsync(CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VideoDatabaseService>();
        var streamCheckService = scope.ServiceProvider.GetRequiredService<IStreamCheckService>();
        var downloadService = scope.ServiceProvider.GetRequiredService<StreamDownloadService>();

        var subscriptions = db.GetAllSubscriptions().Where(s => s.IsEnabled).ToList();
        var activeJobs = db.GetActiveDownloadJobs().ToList();

        foreach (var sub in subscriptions)
        {
            if (stoppingToken.IsCancellationRequested) break;

            // Check if it's time to check
            if (sub.LastCheckedAt.HasValue && 
                DateTime.Now < sub.LastCheckedAt.Value.AddMinutes(sub.CheckRateMinutes))
            {
                continue;
            }

            // Check if there is already an active job for this URL
            if (activeJobs.Any(j => j.Url == sub.Url))
            {
                _logger.LogDebug("Subscription {SubName} has an active job, skipping check.", sub.Name);
                sub.LastCheckedAt = DateTime.Now;
                db.UpsertSubscription(sub);
                continue;
            }

            _logger.LogInformation("Checking subscription: {SubName} ({SubUrl})", sub.Name, sub.Url);
            
            bool isLive = await streamCheckService.IsLiveAsync(sub.Url);
            sub.LastCheckedAt = DateTime.Now;

            if (isLive)
            {
                _logger.LogInformation("Stream is live for subscription: {SubName}. Starting download.", sub.Name);
                await downloadService.StartDownloadAsync(sub.Url, sub.Resolution);
                sub.LastTriggeredAt = DateTime.Now;
            }

            db.UpsertSubscription(sub);
        }
    }
}
