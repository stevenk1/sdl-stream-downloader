using System.Threading.Channels;
using SDL.Models;

namespace SDL.Services.Queues;

public interface IConversionQueue
{
    ValueTask QueueConversionAsync(ConversionJob job);
    ValueTask<ConversionJob> DequeueConversionAsync(CancellationToken cancellationToken);
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
