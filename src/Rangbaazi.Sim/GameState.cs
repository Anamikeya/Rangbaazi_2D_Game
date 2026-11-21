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

    /// <summary>
    /// Prop Boxes, loose weapons and projectiles (Weapons.cs): a fixed array, cheap to copy, sized
    /// when the match is made (<see cref="Weapons.PropSlots"/>).
    /// </summary>
    public Prop[] Props;

    /// <summary>Frames until the next Prop Box drops, and the weapon the last one held.</summary>
    public int PropTimer;
    public WeaponId LastBoxWeapon;

    /// <summary>
    /// Each fighter's match record, for the winner's nickname: <see cref="Weapons.RecordInts"/>
    /// ints a fighter, six slots of [weapon + 1 (0 is fists, so 1), damage dealt to others, KOs,
    /// sweet-spot hits] and then all the damage they have done. Read it with
    /// <see cref="Simulation.WeaponStat"/> and <see cref="Simulation.MainWeapon"/>. In the rollback
    /// state so a rollback never counts a hit twice.
    /// </summary>
    public int[] WeaponStats;

    public GameState(int fighterCount, ulong seed)
    {
        Fighters = new Fighter[fighterCount];
        Props = new Prop[Weapons.PropSlots(fighterCount)];
        WeaponStats = new int[fighterCount * Weapons.RecordInts];
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
        Array.Copy(other.Props, Props, Props.Length);
        PropTimer = other.PropTimer;
        LastBoxWeapon = other.LastBoxWeapon;
        Array.Copy(other.WeaponStats, WeaponStats, WeaponStats.Length);
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
        h.Add(PropTimer);
        h.Add((int)LastBoxWeapon);
        for (int i = 0; i < Props.Length; i++) Props[i].AddTo(ref h);
        for (int i = 0; i < WeaponStats.Length; i++) h.Add(WeaponStats[i]);
        return h.Value;
    }
}
