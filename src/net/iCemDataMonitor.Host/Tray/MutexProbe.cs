using System.Threading;

namespace iCemDataMonitor.Host.Tray;

/// <summary>
/// Live probe: is the named mutex held by another process? Formerly the
/// Program.IsMutexTaken local function; extracted in W21.3 because BOTH the
/// tray [Открыть панель] path AND the NavigateUi RPC need the same
/// ui-alive truth. A mutex that exists but cannot be opened is treated as
/// TAKEN (fail-safe); an ABANDONED mutex (previous owner died) counts as FREE.
/// </summary>
public static class MutexProbe
{
    public static bool IsTaken(string name)
    {
        Mutex mutex;
        try
        {
            mutex = Mutex.OpenExisting(name);
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false; // nobody created it → free
        }
        catch (UnauthorizedAccessException)
        {
            return true; // exists but inaccessible → assume taken (fail-safe)
        }

        try
        {
            try
            {
                if (!mutex.WaitOne(TimeSpan.Zero))
                    return true;
            }
            catch (AbandonedMutexException)
            {
                return false; // owner died — mutex is free again
            }
            try { mutex.ReleaseMutex(); } catch (ApplicationException) { /* not owned */ }
            return false;
        }
        finally
        {
            mutex.Dispose();
        }
    }
}
