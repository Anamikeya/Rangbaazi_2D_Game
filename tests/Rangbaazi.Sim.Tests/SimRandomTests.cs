namespace Rangbaazi.Sim.Tests;

public class SimRandomTests
{
    [Fact]
    public void SameSeedSameSequence()
    {
        var a = new SimRandom(1234);
        var b = new SimRandom(1234);
        for (int i = 0; i < 1000; i++) Assert.Equal(a.NextULong(), b.NextULong());
    }

    [Fact]
    public void KnownFirstValueIsStable()
    {
        // Pinned so an accidental algorithm change (which would break replays) fails loudly.
        var r = new SimRandom(0);
        Assert.Equal(0xE220A8397B1DCDAFUL, r.NextULong());
    }

    [Fact]
    public void RangeStaysInBounds()
    {
        var r = new SimRandom(99);
        for (int i = 0; i < 10_000; i++) Assert.InRange(r.Range(-3, 5), -3, 4);
    }

    [Fact]
    public void CopyingStructForksTheStream()
    {
        var r = new SimRandom(7);
        r.NextULong();
        SimRandom saved = r;            // what a rollback "save state" does
        ulong expected = r.NextULong();
        Assert.Equal(expected, saved.NextULong());
    }
}
