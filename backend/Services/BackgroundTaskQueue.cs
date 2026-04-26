using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace NzbWebDAV.Services;

public sealed class BackgroundTaskQueue
{
    private const int QueueCapacity = 256;
    private readonly Channel<BackgroundTaskWorkItem> _queue;

    public BackgroundTaskQueue()
    {
        _queue = Channel.CreateBounded<BackgroundTaskWorkItem>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });
    }

    public bool TryQueue(string description, Func<CancellationToken, Task> workItem)
    {
        ArgumentNullException.ThrowIfNull(workItem);

        var queued = _queue.Writer.TryWrite(new BackgroundTaskWorkItem(description, workItem, DateTimeOffset.UtcNow));
        if (!queued)
        {
            Log.Warning("[BackgroundTaskQueue] Queue is full or stopping; dropping background job: {Description}", description);
        }

        return queued;
    }

    public bool TryQueueDelayed(string description, TimeSpan delay, Func<CancellationToken, Task> workItem)
    {
        return TryQueue(description, async ct =>
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
            await workItem(ct).ConfigureAwait(false);
        });
    }

    internal ChannelReader<BackgroundTaskWorkItem> Reader => _queue.Reader;

    internal void Complete() => _queue.Writer.TryComplete();
}

internal sealed record BackgroundTaskWorkItem(
    string Description,
    Func<CancellationToken, Task> WorkItem,
    DateTimeOffset QueuedAt);

public sealed class BackgroundTaskQueueService(BackgroundTaskQueue queue) : IHostedService
{
    private const int WorkerCount = 4;
    private readonly CancellationTokenSource _stoppingCts = new();
    private Task[] _workers = [];

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _workers = Enumerable.Range(1, WorkerCount)
            .Select(workerId => Task.Run(() => WorkerLoopAsync(workerId, _stoppingCts.Token), CancellationToken.None))
            .ToArray();

        Log.Information("[BackgroundTaskQueue] Started {WorkerCount} supervised background workers", WorkerCount);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        queue.Complete();

        try
        {
            await Task.WhenAll(_workers).WaitAsync(cancellationToken).ConfigureAwait(false);
            Log.Information("[BackgroundTaskQueue] Drained all queued background jobs");
        }
        catch (OperationCanceledException)
        {
            Log.Warning("[BackgroundTaskQueue] Shutdown timeout while draining background jobs; cancelling remaining work");
            _stoppingCts.Cancel();

            try
            {
                await Task.WhenAll(_workers).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[BackgroundTaskQueue] Worker stopped after forced cancellation");
            }
        }
        finally
        {
            _stoppingCts.Dispose();
        }
    }

    private async Task WorkerLoopAsync(int workerId, CancellationToken stoppingToken)
    {
        try
        {
            while (await queue.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
            {
                while (queue.Reader.TryRead(out var workItem))
                {
                    var waitTime = DateTimeOffset.UtcNow - workItem.QueuedAt;
                    try
                    {
                        Log.Debug("[BackgroundTaskQueue] Worker {WorkerId} starting {Description} after {WaitMs}ms queued",
                            workerId, workItem.Description, waitTime.TotalMilliseconds);

                        await workItem.WorkItem(stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        Log.Information("[BackgroundTaskQueue] Cancelled background job during shutdown: {Description}", workItem.Description);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "[BackgroundTaskQueue] Background job failed: {Description}", workItem.Description);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal forced shutdown path.
        }
    }
}
