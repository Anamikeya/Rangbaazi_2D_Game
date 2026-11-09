using Godot;
using Rangbaazi.Net;

namespace Rangbaazi.Game.Diagnostics;

/// <summary>
/// The online smoke test (<c>tools/godot.ps1 online-smoke</c>): two game processes, each with a
/// bot playing its side, connect over UDP on 127.0.0.1. Each passes once <c>--frames</c> frames
/// are final, at least one checksum has been compared with the opponent's and matched, and an
/// offline replay of the confirmed inputs reaches the same checksum at that frame. It then prints
/// <c>ONLINE SMOKE PASS ... checksum=...</c>, which the script compares between the two
/// processes, and keeps playing for a moment so the slower process can pass too.
/// Exit code 0 = pass, 1 = fail (the match ended early, or two minutes went by).
/// </summary>
public sealed class OnlineSmokeTest
{
    private const int GraceTicks = 120;
    private const ulong TimeoutMs = 120_000;

    private readonly int _frames;
    private readonly ulong _startMs = Time.GetTicksMsec();
    private string? _passLine;
    private int _graceLeft = GraceTicks;

    public OnlineSmokeTest(int frames) => _frames = frames;

    /// <summary>Call after every tick; returns the exit code once the test is over.</summary>
    public int? Check(OnlineMatch m)
    {
        if (_passLine != null)
        {
            if (m.EndReason == OnlineEndReason.Desync) return Fail(m, m.EndMessage);
            if (--_graceLeft > 0 && m.Phase != OnlinePhase.Ended) return null;
            GD.Print(_passLine);
            return 0;
        }

        if (m.Phase == OnlinePhase.Ended) return Fail(m, m.EndMessage);
        if (Time.GetTicksMsec() - _startMs > TimeoutMs) return Fail(m, $"timed out in phase {m.Phase} at frame {m.FinalFrame}");
        if (m.FinalFrame < _frames || m.ChecksumsVerified == 0) return null;

        if (!m.Checksums!.TryGet(_frames, out ulong checksum)) return Fail(m, $"no checksum recorded for frame {_frames}");
        ulong replayed = m.ConfirmedInputs!.Replay(m.Rules, m.Seed, _frames).Checksum();
        if (replayed != checksum) return Fail(m, $"offline replay reaches {replayed:X16}, online {checksum:X16}");

        _passLine = $"ONLINE SMOKE PASS side=P{m.LocalSlot + 1} frame={_frames} checksum={checksum:X16} seed={m.Seed:X16} " +
            $"verified={m.ChecksumsVerified} rollbacks={m.Rollbacks.Count} maxRollback={m.Rollbacks.Max} medianRollback={m.Rollbacks.Median} " +
            $"timeSyncSkips={m.TimeSyncTicksSkipped}";
        return null;
    }

    private static int Fail(OnlineMatch m, string reason)
    {
        GD.PushError($"ONLINE SMOKE FAIL side=P{m.LocalSlot + 1} frame={m.FinalFrame}: {reason}");
        return 1;
    }
}
