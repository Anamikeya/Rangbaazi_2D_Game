using Backdash;
using Backdash.Core;
using Backdash.Network;
using Rangbaazi.Sim;
using Rangbaazi.Sim.Diagnostics;

namespace Rangbaazi.Net.Tests;

/// <summary>
/// Backdash's SyncTest mode rolls back and re-simulates every few frames and compares checksums.
/// It runs our real rollback handler (save, load, re-simulate) without a network.
/// </summary>
public class SyncTestTests
{
    private static readonly MatchRules Rules = new() { Stage = Stages.Graybox };

    private static void Run(RollbackHandler handler, int checkDistance, int frames, bool bots)
    {
        using INetcodeSession<Buttons> session = RollbackNetcode.WithInputType<Buttons>()
            .WithPlayerCount(2)
            .WithHandler(handler)
            .WithChecksumProvider(static data => GameStateCodec.Fold(data))
            .WithLogLevel(LogLevel.Error)
            .WithLogWriter((_, _) => { })
            .Configure(n =>
            {
                n.PredictionFrames = 8;
                n.StateSerializationEndianness = Endianness.LittleEndian;
            })
            .ForSyncTest(o =>
            {
                o.CheckDistanceFrames = checkDistance;
                o.ThrowOnDesync = true;
                o.LogStateOnDesync = false;
            })
            .Build();
        handler.Attach(session);
        session.Start();

        NetcodePlayer[] players = session.GetPlayers().OrderBy(p => p.Index).ToArray();
        var rng = new SimRandom(77);
        GameState state = handler.State;
        for (int f = 0; f < frames; f++)
        {
            session.BeginFrame();
            for (int p = 0; p < players.Length; p++)
            {
                Buttons b = bots ? SparringBot.Think(state, Rules, p, CpuLevel.Hard) : (Buttons)(rng.NextUInt() & 0x7FF);
                Assert.Equal(ResultCode.Ok, session.AddLocalInput(players[p], b));
            }
            Assert.Equal(ResultCode.Ok, session.SynchronizeInputs());
            handler.Step();
            session.AdvanceFrame(); // throws NetcodeException when a re-simulation differs
        }
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(7, true)]
    [InlineData(1, false)]
    [InlineData(7, false)]
    public void RollingBackNeverChangesTheMatch(int checkDistance, bool bots)
    {
        var handler = new RollbackHandler(Simulation.CreateInitialState(Rules, 2, 42), Rules);
        Run(handler, checkDistance, frames: 1500, bots);
        Assert.Equal(1500, handler.State.Frame);
        Assert.True(handler.Rollbacks.Count > 0);
        Assert.InRange(handler.Rollbacks.Max, checkDistance, checkDistance);
    }

    [Fact]
    public void StateKeptOutsideGameStateIsCaught()
    {
        // A handler that changes the match from a counter Backdash can't save or restore: a
        // re-simulated frame then differs from the original, which SyncTest must report.
        int calls = 0;
        var handler = new RollbackHandler(Simulation.CreateInitialState(Rules, 2, 42), Rules)
        {
            AfterAdvance = s =>
            {
                if (++calls == 101) s.Fighters[0].Josh += 5;
            },
        };
        Assert.Throws<NetcodeException>(() => Run(handler, checkDistance: 1, frames: 300, bots: true));
    }
}
