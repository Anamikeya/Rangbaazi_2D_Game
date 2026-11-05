using System;
using System.Text;
using Godot;
using Rangbaazi.Game.Controls;
using Rangbaazi.Sim;
using Rangbaazi.Sim.Diagnostics;

namespace Rangbaazi.Game.Diagnostics;

/// <summary>
/// In-engine smoke test, enabled with <c>godot --path game --headless --fixed-fps 60 -- --smoke</c>
/// (or <c>tools/godot.ps1 smoke</c>). Two <see cref="SparringBot"/>s play <see cref="SmokeScript"/>
/// through the real input path:
///
///   bot Buttons -> synthetic InputEventKey (P1/P2 default keys) -> Godot Input
///     -> InputBindings.Read -> Simulation.Advance
///
/// Each frame it checks that the buttons read back equal the buttons the bots asked for. At the
/// end it checks that the match equals a pure-sim replay of the same script in this process (same
/// checksum) and that the quick-chat key reached the chat hub. It always ends the process: exit
/// code 0 = pass, 1 = fail, including when something throws or the match stops advancing.
///
/// Windowed runs (e.g. <c>tools/godot.ps1 record</c>) also see real keyboards and pads, so don't
/// touch them while it runs. Losing window focus is handled: keys are re-pressed every tick.
/// Presentation-side only: it produces inputs, it never writes GameState.
/// </summary>
public sealed class SmokeTest
{
    public const string Flag = "--smoke";

    /// <summary>Fail if Godot has run this many more physics ticks than the sim has advanced.</summary>
    private const int StallTicks = 120;

    private static readonly Buttons[] DrivenButtons =
        { Buttons.Left, Buttons.Right, Buttons.Up, Buttons.Down, Buttons.Jump, Buttons.Light };

    private readonly Buttons[] _wanted = new Buttons[SmokeScript.Players];
    private readonly ulong _physicsFrameAtStart = Engine.GetPhysicsFrames();
    private int _mismatches;
    private string? _firstMismatch;

    public static bool Requested() => Array.IndexOf(OS.GetCmdlineUserArgs(), Flag) >= 0;

    /// <summary>
    /// Call before reading input: presses/releases keys so Godot's key state matches the bots.
    /// Compares against Godot's actual key state, not a cache, so keys released by focus loss
    /// are pressed again on the next tick.
    /// </summary>
    public void PressKeys(GameState state, MatchRules rules)
    {
        SmokeScript.Inputs(state, rules, _wanted);

        for (int p = 0; p < _wanted.Length; p++)
        {
            foreach (Buttons button in DrivenButtons)
            {
                if (!InputBindings.TryGetDefaultKey(p, button, out Key key))
                    throw new InvalidOperationException($"P{p + 1} has no default key for {button}.");
                bool want = (_wanted[p] & button) != 0;
                if (want != Input.IsPhysicalKeyPressed(key)) SendKey(key, want);
            }
        }

        if (state.Frame == SmokeScript.QuickChatFrame) SendKey(Key.T, true);
        if (state.Frame == SmokeScript.QuickChatFrame + 1) SendKey(Key.T, false);

        // Apply now instead of next frame, so this tick's InputBindings.Read sees it.
        Input.FlushBufferedEvents();
    }

    /// <summary>Call after reading input: records any difference from what the bots pressed.</summary>
    public void CheckRead(int frame, ReadOnlySpan<Buttons> read)
    {
        for (int p = 0; p < _wanted.Length; p++)
        {
            if (read[p] == _wanted[p]) continue;
            _mismatches++;
            _firstMismatch ??= $"frame {frame} P{p + 1}: wanted {_wanted[p]}, read {read[p]}";
        }
    }

    /// <summary>True when the sim has stopped advancing (e.g. every tick throws before Advance).</summary>
    public bool Stalled(int simFrame) =>
        Engine.GetPhysicsFrames() - _physicsFrameAtStart > (ulong)(simFrame + StallTicks);

    /// <summary>Releases held keys, prints the report and returns the process exit code.</summary>
    public int Finish(GameState state, MatchRules rules, int chatMessages)
    {
        ReleaseAll();

        ulong expected = SmokeScript.Run(rules, state.Frame).Checksum();
        ulong actual = state.Checksum();

        var report = new StringBuilder();
        bool ok = true;
        if (_mismatches > 0)
        {
            ok = false;
            report.Append($" inputMismatches={_mismatches} (first: {_firstMismatch})");
        }
        if (actual != expected)
        {
            ok = false;
            report.Append($" expectedChecksum={expected:X16}");
        }
        if (chatMessages == 0)
        {
            ok = false;
            report.Append(" quickChat=not-received");
        }

        int stocksLost = 0;
        foreach (Fighter f in state.Fighters) stocksLost += rules.Stocks - f.Stocks;

        string line = $"SMOKE {(ok ? "PASS" : "FAIL")} frames={state.Frame} checksum={actual:X16} " +
            $"stocksLost={stocksLost} chatMessages={chatMessages}{report}";
        if (ok) GD.Print(line);
        else GD.PushError(line);
        return ok ? 0 : 1;
    }

    /// <summary>Reports a failure that stopped the run early and returns the exit code (1).</summary>
    public int Abort(int simFrame, string reason)
    {
        try { ReleaseAll(); }
        catch (Exception) { /* already failing; the reason below is what matters */ }
        GD.PushError($"SMOKE FAIL frames={simFrame} aborted: {reason}");
        return 1;
    }

    private void ReleaseAll()
    {
        for (int p = 0; p < _wanted.Length; p++)
            foreach (Buttons button in DrivenButtons)
                if (InputBindings.TryGetDefaultKey(p, button, out Key key) && Input.IsPhysicalKeyPressed(key))
                    SendKey(key, false);
        Input.FlushBufferedEvents();
    }

    private static void SendKey(Key key, bool pressed) =>
        Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = key, Pressed = pressed });
}
