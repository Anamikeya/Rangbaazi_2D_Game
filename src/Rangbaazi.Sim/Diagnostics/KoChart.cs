using System.Text;
using Rangbaazi.Sim.FixedMath;

namespace Rangbaazi.Sim.Diagnostics;

/// <summary>
/// How much Josh each move needs to KO, measured in the sim, for the KO table in
/// docs/design/combat-spec-v0.1.md (a test keeps that table equal to <see cref="MarkdownTable"/>).
///
/// Setup: a fresh two-fighter match on the rules' stage. The defender stands at the centre, or
/// 1 m inside the right edge, with the attacker to its left facing it; the attacker does the move
/// once and then nothing. "Recovers" means that after the hit, whenever it can act (including
/// steering during its up heavy), the defender off the stage presses what the CPU would
/// (<see cref="SparringBot"/>: air jump, up heavy, air dodge, steering home) and over the stage
/// drifts toward the centre, never attacking. "No recovery" means it presses nothing. A case ends
/// when the defender loses the stock, or is standing still on a platform and able to act again.
/// The result is the lowest Josh, before the hit, at which the defender loses the stock.
/// </summary>
public static class KoChart
{
    /// <summary>Lowest Josh (before the hit) at which the move KOs, or -1 if none up to MaxJosh.</summary>
    public static int LowestKoJosh(MatchRules rules, MoveId move, bool nearEdge, bool defenderRecovers)
    {
        for (int josh = 0; josh <= rules.MaxJosh; josh++)
            if (KnocksOut(rules, move, nearEdge, defenderRecovers, josh)) return josh;
        return -1;
    }

    /// <summary>The table for the spec: every move, recovering defender (centre, near the edge), then no recovery from the centre.</summary>
    public static string MarkdownTable(MatchRules rules)
    {
        var sb = new StringBuilder();
        sb.Append("| Move | From the centre | From 1 m inside the edge | From the centre, no recovery |\n");
        sb.Append("|---|---|---|---|\n");
        foreach (MoveId move in Moves)
        {
            sb.Append("| ").Append(Name(move))
              .Append(" | ").Append(Cell(LowestKoJosh(rules, move, nearEdge: false, defenderRecovers: true)))
              .Append(" | ").Append(Cell(LowestKoJosh(rules, move, nearEdge: true, defenderRecovers: true)))
              .Append(" | ").Append(Cell(LowestKoJosh(rules, move, nearEdge: false, defenderRecovers: false)))
              .Append(" |\n");
        }
        return sb.ToString();
    }

    private static readonly MoveId[] Moves =
        { MoveId.Jab, MoveId.NeutralHeavy, MoveId.SideHeavy, MoveId.DownHeavy, MoveId.UpHeavy, MoveId.GroundSlam };

    private static string Name(MoveId move) => move switch
    {
        MoveId.Jab => "Jab",
        MoveId.NeutralHeavy => "Neutral heavy",
        MoveId.SideHeavy => "Side heavy",
        MoveId.DownHeavy => "Down heavy",
        MoveId.UpHeavy => "Up heavy",
        MoveId.GroundSlam => "Ground slam",
        _ => move.ToString(),
    };

    private static string Cell(int josh) => josh < 0 ? "no KO" : josh + "%";

    /// <summary>Button to press, and where the attacker starts relative to the defender (cm), for each move to land.</summary>
    private static void Setup(MoveId move, out Buttons press, out int dxCm, out int heightCm)
    {
        heightCm = 0;
        switch (move)
        {
            case MoveId.Jab: press = Buttons.Light; dxCm = 80; break;
            case MoveId.NeutralHeavy: press = Buttons.Heavy; dxCm = 100; break;
            case MoveId.SideHeavy: press = Buttons.Heavy | Buttons.Right; dxCm = 150; break;
            case MoveId.DownHeavy: press = Buttons.Heavy | Buttons.Down; dxCm = 80; break;
            case MoveId.UpHeavy: press = Buttons.Heavy | Buttons.Up; dxCm = 50; break;
            default: press = Buttons.Heavy | Buttons.Down; dxCm = 0; heightCm = 300; break; // ground slam from above
        }
    }

    private static bool KnocksOut(MatchRules rules, MoveId move, bool nearEdge, bool defenderRecovers, int josh)
    {
        Setup(move, out Buttons press, out int dxCm, out int heightCm);
        FixRect stage = rules.Stage.Platforms[0];
        Fix defenderX = nearEdge ? stage.MaxX - Fix.One : Fix.Zero;

        GameState s = Simulation.CreateInitialState(rules, 2, 1);
        ref Fighter a = ref s.Fighters[0];
        a.Position = new FixVec2(defenderX - Fix.FromRatio(dxCm, 100), Fix.FromRatio(heightCm, 100));
        a.Velocity = FixVec2.Zero;
        a.Grounded = heightCm == 0;
        a.Facing = 1;
        ref Fighter d = ref s.Fighters[1];
        d.Position = new FixVec2(defenderX, Fix.Zero);
        d.Velocity = FixVec2.Zero;
        d.Grounded = true;
        d.Facing = -1;
        d.Josh = josh;

        var inputs = new Buttons[2];
        inputs[0] = press;
        Simulation.Advance(s, rules, inputs);
        inputs[0] = Buttons.None;

        bool hit = false;
        for (int f = 0; f < 900; f++)
        {
            Fighter def = s.Fighters[1];
            if (def.Stocks < rules.Stocks) return true;
            if (def.Action == FighterAction.Hitstun) hit = true;
            if (hit && def.Grounded && def.Action == FighterAction.Idle && def.Velocity.X == Fix.Zero) return false; // landed and stopped
            bool acts = def.Action is FighterAction.Idle or FighterAction.Attack;
            inputs[1] = defenderRecovers && hit && acts ? Recover(s, rules, stage) : Buttons.None;
            Simulation.Advance(s, rules, inputs);
        }
        return s.Fighters[1].Stocks < rules.Stocks;
    }

    /// <summary>The CPU's buttons when off the stage (recovery, or steering during its up heavy); otherwise drift toward the centre to land.</summary>
    private static Buttons Recover(GameState s, MatchRules rules, FixRect stage)
    {
        ref readonly Fighter me = ref s.Fighters[1];
        bool off = me.Position.X < stage.MinX || me.Position.X > stage.MaxX || (!me.Grounded && me.Position.Y < stage.MaxY);
        if (off) return SparringBot.Think(s, rules, 1, CpuLevel.Hard);
        return me.Position.X > Fix.Zero ? Buttons.Left : Buttons.Right;
    }
}
