// SPDX-License-Identifier: MIT
using AgentKick75.App.Infrastructure;

namespace AgentKick75.App.Hosting;

public sealed class SingleInstanceLease : IDisposable
{
    private readonly Mutex mutex;
    private bool disposed;

    private SingleInstanceLease(Mutex mutex)
    {
        this.mutex = mutex;
    }

    public static bool TryAcquire(out SingleInstanceLease? lease, string? mutexName = null)
    {
        var mutex = new Mutex(
            initiallyOwned: true,
            mutexName ?? UserScope.MutexName,
            out bool createdNew);

        if (!createdNew)
        {
            mutex.Dispose();
            lease = null;
            return false;
        }

        lease = new SingleInstanceLease(mutex);
        return true;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
