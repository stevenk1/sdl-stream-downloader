using System.Threading.Channels;
using SDL.Models;

namespace SDL.Services;

public interface IDownloadQueue
{
    ValueTask QueueDownloadAsync(DownloadJob job);
    ValueTask<DownloadJob> DequeueDownloadAsync(CancellationToken cancellationToken);
}

public interface IConversionQueue
{
    ValueTask QueueConversionAsync(ConversionJob job);
    ValueTask<ConversionJob> DequeueConversionAsync(CancellationToken cancellationToken);
}

public class DownloadQueue : IDownloadQueue
{
    private readonly Channel<DownloadJob> _queue;

    public DownloadQueue()
    {
        _queue = Channel.CreateUnbounded<DownloadJob>();
    }

    public async ValueTask QueueDownloadAsync(DownloadJob job)
    {
        await _queue.Writer.WriteAsync(job);
    }

    public async ValueTask<DownloadJob> DequeueDownloadAsync(CancellationToken cancellationToken)
    {
        return await _queue.Reader.ReadAsync(cancellationToken);
    }
}

public class ConversionQueue : IConversionQueue
{
    private readonly Channel<ConversionJob> _queue;

    public ConversionQueue()
    {
        _queue = Channel.CreateUnbounded<ConversionJob>();
    }

    public async ValueTask QueueConversionAsync(ConversionJob job)
    {
        await _queue.Writer.WriteAsync(job);
    }

    public async ValueTask<ConversionJob> DequeueConversionAsync(CancellationToken cancellationToken)
    {
        return await _queue.Reader.ReadAsync(cancellationToken);
    }
}
