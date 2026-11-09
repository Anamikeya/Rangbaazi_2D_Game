using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Backdash;
using Backdash.Synchronizing.Input.Confirmed;
using Rangbaazi.Sim;

namespace Rangbaazi.Net;

/// <summary>
/// Every frame's final inputs, in order from frame 0, as Backdash confirms them (both players'
/// input for the frame has arrived). Replaying them offline reproduces the match exactly, which
/// is how tests prove the online timeline equals the offline one, and how a desync can be
/// investigated later. Backdash calls <see cref="OnConfirmed"/> on the game thread, inside
/// BeginFrame, after any rollback for those inputs.
/// </summary>
public sealed class ConfirmedInputLog : IInputListener<Buttons>
{
    private readonly List<Buttons> _inputs = new(2 * 60 * 60 * 4);

    public ConfirmedInputLog(int players) => Players = players;

    public int Players { get; }

    /// <summary>Number of frames confirmed so far (frames 0 to Frames - 1).</summary>
    public int Frames => _inputs.Count / Players;

    /// <summary>The confirmed inputs of one frame, indexed by player.</summary>
    public ReadOnlySpan<Buttons> this[int frame] => CollectionsMarshal.AsSpan(_inputs).Slice(frame * Players, Players);

    void IInputListener<Buttons>.OnSessionStart(InputContext<Buttons> context)
    {
    }

    public void OnConfirmed(in Frame frame, in ConfirmedInputs<Buttons> inputs)
    {
        if (frame.Number != Frames)
            throw new InvalidOperationException($"Backdash confirmed frame {frame.Number} out of order (expected {Frames}).");
        Span<Buttons> all = stackalloc Buttons[InputArray<Buttons>.Capacity];
        inputs.CopyTo(all);
        for (int p = 0; p < Players; p++) _inputs.Add(all[p]);
    }

    void IInputListener<Buttons>.OnSessionClose()
    {
    }

    void IDisposable.Dispose()
    {
    }

    /// <summary>Replays the first <paramref name="frames"/> confirmed frames offline from a fresh match.</summary>
    public GameState Replay(MatchRules rules, ulong seed, int frames)
    {
        if (frames > Frames) throw new ArgumentOutOfRangeException(nameof(frames), $"Only {Frames} frames are confirmed.");
        GameState state = Simulation.CreateInitialState(rules, Players, seed);
        for (int f = 0; f < frames; f++) Simulation.Advance(state, rules, this[f]);
        return state;
    }
}
