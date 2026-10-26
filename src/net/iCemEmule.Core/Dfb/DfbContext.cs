// DfbContext — execution context одного тика сети (1 Hz).
// Детерминизм: seeded Random; ¬ DateTime, ¬ Guid, ¬ ambient state.

namespace Halliburton.iCemEmule.Dfb;

public sealed class DfbContext
{
    public double ElapsedSeconds { get; }
    public double Dt { get; }
    public long Tick { get; }
    public Random Random { get; }

    private DfbContext(double elapsedSeconds, double dt, long tick, int seed)
    {
        ElapsedSeconds = elapsedSeconds;
        Dt = dt;
        Tick = tick;
        Random = new Random(seed);
    }

    public static DfbContext Create(double elapsedSeconds, long tick, int seed)
        => new(elapsedSeconds, 1.0, tick, CombineSeed(seed, tick));

    public static DfbContext Create(double elapsedSeconds, double dt, long tick, int seed)
        => new(elapsedSeconds, dt, tick, CombineSeed(seed, tick));

    // INT_DfbEngine: Random seeded by tick ⇒ шум варьируется по тикам, детерминизм сохранён
    private static int CombineSeed(int seed, long tick)
        => unchecked(seed * 31 + (int)(tick & 0x7FFFFFFF));
}
