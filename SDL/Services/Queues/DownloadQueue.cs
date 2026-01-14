using System.Threading.Channels;
using SDL.Models;

namespace SDL.Services.Queues;

public interface IDownloadQueue
{
    ValueTask QueueDownloadAsync(DownloadJob job);
    ValueTask<DownloadJob> DequeueDownloadAsync(CancellationToken cancellationToken);
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
