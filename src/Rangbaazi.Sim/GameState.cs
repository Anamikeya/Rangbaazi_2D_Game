using System;

namespace Rangbaazi.Sim;

/// <summary>
/// Everything that changes during a match. Rollback = Clone() to save, CopyFrom() to load,
/// then re-run <see cref="Simulation.Advance"/>. Never put rendering, audio or ragdoll data here.
/// </summary>
public sealed class GameState
{
    public int Frame;
    public SimRandom Random;
    public Fighter[] Fighters;
    public bool MatchOver;
    public int WinnerIndex = -1;

    public GameState(int fighterCount, ulong seed)
    {
        Fighters = new Fighter[fighterCount];
        Random = new SimRandom(seed);
    }

    public GameState Clone()
    {
        var copy = new GameState(Fighters.Length, 0);
        copy.CopyFrom(this);
        return copy;
    }

    public void CopyFrom(GameState other)
    {
        if (other.Fighters.Length != Fighters.Length)
            throw new ArgumentException("Fighter count mismatch.", nameof(other));
        Frame = other.Frame;
        Random = other.Random;
        Array.Copy(other.Fighters, Fighters, Fighters.Length);
        MatchOver = other.MatchOver;
        WinnerIndex = other.WinnerIndex;
    }

    /// <summary>Stable checksum used for desync detection between peers.</summary>
    public ulong Checksum()
    {
        var h = new StateHasher();
        h.Add(Frame);
        h.Add(Random.State);
        h.Add(MatchOver);
        h.Add(WinnerIndex);
        for (int i = 0; i < Fighters.Length; i++) Fighters[i].AddTo(ref h);
        return h.Value;
    }
}
