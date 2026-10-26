using System;
using System.Collections.Generic;
using System.Threading;

namespace Burservis.iCem.DataMonitor.Bus
{
    /// <summary>
    /// Single-consumer work queue for bus writes. All bus mutex I/O happens on
    /// ONE background thread — vendor threads (UI, publisher STA, timers) only
    /// post O(1) actions and NEVER block on the bus mutex. Bounded drop-oldest.
    /// </summary>
    internal static class BusWriteQueue
    {
        private const int MaxItems = 1024;

        private static readonly object Gate = new object();
        private static readonly Queue<Action> Items = new Queue<Action>(MaxItems);
        private static Thread _worker;
        private static long _dropped;

        public static long Dropped
        {
            get { return Interlocked.Read(ref _dropped); }
        }

        public static void Post(Action action)
        {
            if (action == null)
            {
                return;
            }
            try
            {
                lock (Gate)
                {
                    while (Items.Count >= MaxItems)
                    {
                        Items.Dequeue();
                        Interlocked.Increment(ref _dropped);
                    }
                    Items.Enqueue(action);
                    Monitor.Pulse(Gate);
                    EnsureStartedLocked();
                }
            }
            catch
            {
                // never propagate to vendor threads
            }
        }

        private static void EnsureStartedLocked()
        {
            if (_worker != null && _worker.IsAlive)
            {
                return;
            }
            _worker = new Thread(Loop) { IsBackground = true, Name = "DataMonitorBusWriter" };
            _worker.Start();
        }

        private static void Loop()
        {
            while (true)
            {
                Action action = null;
                lock (Gate)
                {
                    while (Items.Count == 0)
                    {
                        if (!Monitor.Wait(Gate, 1000))
                        {
                            break;
                        }
                    }
                    if (Items.Count > 0)
                    {
                        action = Items.Dequeue();
                    }
                }
                if (action == null)
                {
                    continue;
                }
                try
                {
                    action();
                }
                catch
                {
                    // swallow-all — bus write failure must not kill the worker
                }
            }
        }
    }
}
