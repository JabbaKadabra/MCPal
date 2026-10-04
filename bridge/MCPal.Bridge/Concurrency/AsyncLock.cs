namespace MCPal.Bridge.Concurrency;

/// <summary>
/// Async mutual exclusion. The only place in the bridge that touches <see cref="SemaphoreSlim"/> directly.
/// </summary>
internal sealed class AsyncLock : IDisposable
{
    private readonly SemaphoreSlim semaphore = new(1, 1);

    public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken);
        return new Releaser(semaphore);
    }

    public void Dispose() => semaphore.Dispose();

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                semaphore.Release();
            }
        }
    }
}
