using System.Threading.Channels;
using SDL.Models;

namespace SDL.Services.Queues;

public interface IArchiveQueue
{
    ValueTask QueueArchiveAsync(DownloadJob job);
    ValueTask<DownloadJob> DequeueArchiveAsync(CancellationToken cancellationToken);
}

public class ArchiveQueue : IArchiveQueue
{
    private readonly Channel<DownloadJob> _queue;

    public ArchiveQueue()
    {
        _queue = Channel.CreateUnbounded<DownloadJob>();
    }

    public async ValueTask QueueArchiveAsync(DownloadJob job)
    {
        await _queue.Writer.WriteAsync(job);
    }

    public async ValueTask<DownloadJob> DequeueArchiveAsync(CancellationToken cancellationToken)
    {
        return await _queue.Reader.ReadAsync(cancellationToken);
    }
}
