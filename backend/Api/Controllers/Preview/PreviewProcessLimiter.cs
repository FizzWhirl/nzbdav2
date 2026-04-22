using System;
using System.Threading;

namespace NzbWebDAV.Api.Controllers.Preview;

internal static class PreviewProcessLimiter
{
    private static readonly int MaxConcurrentProcesses = ResolveMaxConcurrentProcesses();
    private static readonly SemaphoreSlim Semaphore = new(MaxConcurrentProcesses, MaxConcurrentProcesses);

    public static int MaxConcurrent => MaxConcurrentProcesses;

    public static async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        await Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser();
    }

    private static int ResolveMaxConcurrentProcesses()
    {
        var raw = Environment.GetEnvironmentVariable("PREVIEW_MAX_FFMPEG_PROCESSES");
        if (int.TryParse(raw, out var parsed))
        {
            return Math.Clamp(parsed, 1, 16);
        }

        return 4;
    }

    private sealed class Releaser : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                Semaphore.Release();
            }
        }
    }
}