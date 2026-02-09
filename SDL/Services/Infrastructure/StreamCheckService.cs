using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Options;
using SDL.Configuration;

namespace SDL.Services.Infrastructure;

public class StreamCheckService : IStreamCheckService
{
    private readonly VideoStorageSettings _settings;
    private readonly ILogger<StreamCheckService> _logger;

    public StreamCheckService(IOptions<VideoStorageSettings> settings, ILogger<StreamCheckService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<bool> IsLiveAsync(string url)
    {
        try
        {
            // Check if it's a live stream using yt-dlp
            var result = await Cli.Wrap(_settings.YtDlpPath)
                .WithArguments(args => args
                    .Add("--simulate")
                    .Add("--quiet")
                    .Add("--print").Add("live_status")
                    .Add(url))
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();

            if (result.ExitCode != 0)
            {
                return false;
            }

            var output = result.StandardOutput.Trim().ToLower();
            return output == "is_live";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error checking if stream is live: {Url}", url);
            return false;
        }
    }
}
