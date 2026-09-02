using System;
using System.Collections.Generic;
using System.Threading;
using Burservis.iCem.DataMonitor.Bus;
using Xunit;

namespace Burservis.iCem.DataMonitor.Tests;

public class BusWriteQueueTests
{
    [Fact]
    public void Post_ExecutesAction_OnWorkerThread()
    {
        string workerName = null;
        bool done = false;
        BusWriteQueue.Post(delegate
        {
            workerName = Thread.CurrentThread.Name;
            done = true;
        });

        Assert.True(WaitUntil(() => done, 2000), "action not executed within timeout");
        Assert.Equal("DataMonitorBusWriter", workerName);
    }

    [Fact]
    public void Post_PreservesOrder()
    {
        var order = new List<int>();
        var gate = new object();
        for (int i = 0; i < 50; i++)
        {
            int captured = i;
            BusWriteQueue.Post(delegate
            {
                lock (gate)
                {
                    order.Add(captured);
                }
            });
        }

        Assert.True(WaitUntil(() => { lock (gate) return order.Count == 50; }, 5000),
            "not all actions executed within timeout");

        lock (gate)
        {
            for (int i = 0; i < 50; i++)
            {
                Assert.Equal(i, order[i]);
            }
        }
    }

    [Fact]
    public void Overflow_DropsOldest()
    {
        // Deterministic overflow: the first action BLOCKS the worker on a gate
        // (verified in-flight before flooding), so the queue physically fills
        // to capacity while 1030 items are posted: 1 in-flight + 1024 queued ⇒ 5 dropped.
        long executed = 0;
        var gate = new ManualResetEventSlim(false);
        var enteredGate = new ManualResetEventSlim(false);
        BusWriteQueue.Post(delegate
        {
            enteredGate.Set();
            gate.Wait();
            Interlocked.Increment(ref executed);
        });
        Assert.True(enteredGate.Wait(2000), "worker did not pick up the blocking action");
        for (int i = 0; i < BusWriteQueueCapacityForTest - 1; i++)
        {
            BusWriteQueue.Post(delegate { Interlocked.Increment(ref executed); });
        }
        gate.Set();

        Assert.True(WaitUntil(() => Interlocked.Read(ref executed) == BusWriteQueueCapacityForTest - 5, 15000),
            "queue did not drain within timeout; executed=" + Interlocked.Read(ref executed));

        // 1030 posts, 1 in-flight, 1024 queue slots ⇒ 5 oldest dropped
        Assert.Equal(5L, BusWriteQueue.Dropped);
    }

    // Queue depth is internal (1024) — exercised via the public Post/Dropped surface.
    private const int BusWriteQueueCapacityForTest = 1030;

    [Fact]
    public void Post_Null_IsNoOp_NoThrow()
    {
        long before = BusWriteQueue.Dropped;
        BusWriteQueue.Post(null);
        Assert.Equal(before, BusWriteQueue.Dropped); // no drop, no exception
    }

    private static bool WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
            {
                return true;
            }
            Thread.Sleep(10);
        }
        return condition();
    }
}
