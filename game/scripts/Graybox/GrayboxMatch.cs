using System.Collections.Generic;
using Godot;
using Rangbaazi.Game.Controls;
using Rangbaazi.Game.Diagnostics;
using Rangbaazi.Game.Util;
using Rangbaazi.Sim;
using Rangbaazi.Sim.Diagnostics;
using Rangbaazi.Sim.FixedMath;
using Rangbaazi.Social;
using Rangbaazi.Social.Chat;
using Rangbaazi.Social.Safety;

namespace Rangbaazi.Game.Graybox;

/// <summary>
/// Graybox test scene (GDD C3 "First 30 days"): capsule-style fighters, local multiplayer,
/// Josh %, ring-out. Steps the deterministic sim once per physics tick (60 Hz) and draws
/// the state with placeholder shapes. Also shows a local quick-chat log to prove the
/// Social module is wired in (press T to send a line as P1).
///
/// P1 is always a person. With <see cref="CpuOpponents"/> on (F2 toggles it), every other
/// player is a <see cref="SparringBot"/> at the current <see cref="CpuLevel"/> (F3 cycles it).
/// The bot only produces that player's buttons from the current state; the sim treats them like
/// any other input. F4 turns the Guts rule on or off and restarts, for comparing in playtests.
///
/// Combat states are drawn from the state alone: hitboxes in red, a dodging fighter faded (with a
/// white outline while a hit would be parried), a parried fighter with a STUN tag, a fighter frozen
/// by a hit it took (knocked back or soaked by armor) shaking, a pending Guts as a gold bar that
/// fills over 3 s, and Guts armor as a gold outline.
/// </summary>
public partial class GrayboxMatch : Node2D
{
    [Export(PropertyHint.Range, "1,4")] public int PlayerCount { get; set; } = 2;
    [Export] public float PixelsPerMetre { get; set; } = 50f;
    [Export] public bool ShowDebug { get; set; } = true;

    /// <summary>Players 2 and up are CPU sparring bots instead of reading keys and pads (F2 toggles).</summary>
    [Export] public bool CpuOpponents { get; set; } = true;

    private static readonly Color[] PlayerColors =
    {
        new("f2a900"), // marigold
        new("1f9bb0"), // peacock blue
        new("e0218a"), // rani pink
        new("7cb518"), // leaf green
    };

    private static readonly Color PlatformColor = new("3a3a48");
    private static readonly Color PlatformEdge = new("e8e6f0");
    private static readonly Color HitboxColor = new(1, 0.2f, 0.2f, 0.55f);
    private static readonly Color Gold = new("ffd23f");

    private MatchRules _rules = new() { Stage = Stages.Graybox };
    private ulong _seed = 1;
    private GameState _state = null!;
    private Buttons[] _inputs = System.Array.Empty<Buttons>();
    private SmokeTest? _smoke;
    private bool _smokeEnded;
    private CpuLevel _cpuLevel = CpuLevel.Normal;
    private bool _guts = true;

    private readonly LocalChatHub _chatHub = new();
    private IChatService _p1Chat = null!;
    private readonly ChatChannelId _lobby = new(ChatChannelKind.Lobby, "graybox");
    private readonly List<string> _chatLog = new();
    private int _nextQuickChat;

    public override void _Ready()
    {
        // One physics tick = one sim frame. Set here rather than in project.godot, because the
        // editor drops settings that equal Godot's defaults (60 is the default today).
        Engine.PhysicsTicksPerSecond = Simulation.TicksPerSecond;

        if (SmokeTest.Requested())
        {
            _smoke = new SmokeTest();
            _rules = SmokeScript.CreateRules();
            _seed = SmokeScript.Seed;
            PlayerCount = SmokeScript.Players;
        }

        // Godot logs exceptions from script callbacks and keeps running, so in smoke mode
        // catch them here and exit 1 instead of hanging.
        try
        {
            InputBindings.Register();
            Restart();

            _p1Chat = _chatHub.Connect(new PlayerId("p1"), "P1", SocialPolicy.Default(AgeBand.Adult));
            _p1Chat.MessageReceived += m =>
            {
                _chatLog.Add($"{m.SenderName}: {m.Text}");
                if (_chatLog.Count > 5) _chatLog.RemoveAt(0);
            };
            _ = _p1Chat.JoinAsync(_lobby);
        }
        catch (System.Exception e) when (_smoke != null)
        {
            EndSmoke(_smoke.Abort(0, $"exception in _Ready: {e}"));
        }
    }

    private void Restart()
    {
        if (_smoke == null) _rules = new MatchRules { Stage = Stages.Graybox, Guts = _guts };
        _state = Simulation.CreateInitialState(_rules, PlayerCount, _seed);
        _inputs = new Buttons[PlayerCount];
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_smoke == null)
        {
            Step();
            return;
        }
        if (_smokeEnded) return;

        try
        {
            Step();
            if (_state.Frame >= SmokeScript.Frames) EndSmoke(_smoke.Finish(_state, _rules, _chatLog.Count));
        }
        catch (System.Exception e)
        {
            EndSmoke(_smoke.Abort(_state?.Frame ?? 0, $"exception in _PhysicsProcess: {e}"));
        }
    }

    public override void _Process(double delta)
    {
        // Smoke watchdog: catches a run that stops advancing without throwing here.
        if (_smoke != null && !_smokeEnded && _smoke.Stalled(_state?.Frame ?? 0))
            EndSmoke(_smoke.Abort(_state?.Frame ?? 0, "the match stopped advancing"));
    }

    /// <summary>
    /// True when this player's buttons come from the CPU. Never in smoke mode: the smoke test
    /// drives every player through real key events.
    /// </summary>
    private bool IsCpu(int player) => _smoke == null && CpuOpponents && player > 0;

    private void Step()
    {
        _smoke?.PressKeys(_state, _rules);
        // Every player's buttons come from the same pre-advance state, CPU players included.
        for (int p = 0; p < _inputs.Length; p++)
            _inputs[p] = IsCpu(p) ? SparringBot.Think(_state, _rules, p, _cpuLevel) : InputBindings.Read(p);
        _smoke?.CheckRead(_state.Frame, _inputs);

        Simulation.Advance(_state, _rules, _inputs);
        QueueRedraw();
    }

    private void EndSmoke(int exitCode)
    {
        if (_smokeEnded) return;
        _smokeEnded = true;
        SetPhysicsProcess(false);
        SetProcess(false);
        GetTree().Quit(exitCode);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (_smoke == null)
        {
            if (e.IsActionPressed(InputBindings.DebugRestart)) Restart();
            if (e.IsActionPressed(InputBindings.DebugToggleCpu)) CpuOpponents = !CpuOpponents;
            if (e.IsActionPressed(InputBindings.DebugCpuLevel)) _cpuLevel = NextLevel(_cpuLevel);
            if (e.IsActionPressed(InputBindings.DebugToggleGuts))
            {
                _guts = !_guts; // a rule of the match, so it starts a new one
                Restart();
            }
        }

        if (e.IsActionPressed(InputBindings.QuickChatWheel))
        {
            var lines = QuickChatCatalog.All;
            _ = _p1Chat.SendQuickChatAsync(_lobby, lines[_nextQuickChat++ % lines.Count].Id);
        }
    }

    private static CpuLevel NextLevel(CpuLevel level) => level switch
    {
        CpuLevel.Easy => CpuLevel.Normal,
        CpuLevel.Normal => CpuLevel.Hard,
        _ => CpuLevel.Easy,
    };

    private string PlayerLabel(int player) => IsCpu(player) ? $"P{player + 1} CPU" : $"P{player + 1}";

    /// <summary>
    /// Draws text centred on <paramref name="centreX"/>. Measures it rather than passing a layout
    /// width, because DrawString clips text that is wider than the width it is given.
    /// </summary>
    private void DrawCentred(Font font, string text, float centreX, float baselineY, int size, Color color)
    {
        float width = font.GetStringSize(text, HorizontalAlignment.Left, -1, size).X;
        DrawString(font, new Vector2(centreX - width / 2, baselineY), text, HorizontalAlignment.Left, -1, size, color);
    }

    private Vector2 ToScreen(FixVec2 p)
    {
        Vector2 size = GetViewportRect().Size;
        var origin = new Vector2(size.X * 0.5f, size.Y * 0.62f);
        Vector2 v = p.ToVector2();
        return new Vector2(origin.X + v.X * PixelsPerMetre, origin.Y - v.Y * PixelsPerMetre);
    }

    private Rect2 ToScreen(FixRect r)
    {
        Vector2 topLeft = ToScreen(new FixVec2(r.MinX, r.MaxY));
        Vector2 bottomRight = ToScreen(new FixVec2(r.MaxX, r.MinY));
        return new Rect2(topLeft, bottomRight - topLeft);
    }

    public override void _Draw()
    {
        Font font = ThemeDB.FallbackFont;

        DrawRect(ToScreen(_rules.Stage.BlastZone), new Color(1, 1, 1, 0.08f), filled: false, width: 2f);

        foreach (FixRect platform in _rules.Stage.Platforms)
        {
            Rect2 r = ToScreen(platform);
            DrawRect(r, PlatformColor);
            DrawLine(r.Position, r.Position + new Vector2(r.Size.X, 0), PlatformEdge, 3f);
        }

        FighterTuning t = _rules.Tuning;
        for (int i = 0; i < _state.Fighters.Length; i++)
        {
            ref readonly Fighter f = ref _state.Fighters[i];
            if (!f.IsActive) continue;
            DrawFighter(font, in f, i, t);
        }

        DrawHud(font);
    }

    private void DrawFighter(Font font, in Fighter f, int i, FighterTuning t)
    {
        Color c = PlayerColors[i % PlayerColors.Length];
        if (f.Action is FighterAction.Hitstun or FighterAction.Stunned) c = c.Lightened(0.5f);
        if (Simulation.IsIntangible(in f, t)) c.A = 0.35f;

        Rect2 body = ToScreen(Simulation.Hurtbox(in f, t));
        // Shake while frozen by a hit it took, knocked back or soaked by Guts armor; not when frozen
        // because its own attack landed or it parried.
        if (f.HitStop > 0 && f.FrozenByHit) body.Position += new Vector2(_state.Frame % 2 == 0 ? 3 : -3, 0);
        DrawRect(body, c);
        if (Simulation.InParryWindow(in f, t)) DrawRect(body.Grow(3), Colors.White, filled: false, width: 3f);
        if (f.ArmorFrames > 0) DrawRect(body.Grow(5), Gold, filled: false, width: 4f);

        Vector2 eye = body.Position + new Vector2(body.Size.X * (f.Facing > 0 ? 0.75f : 0.25f), body.Size.Y * 0.2f);
        DrawCircle(eye, 5f, Colors.Black);

        if (Simulation.TryGetActiveHitbox(in f, t, out FixRect hitbox)) DrawRect(ToScreen(hitbox), HitboxColor);

        // Pending Guts: a bar under the feet that fills over the 3 s it takes to pay out.
        if (f.GutsTimer > 0)
        {
            float filled = 1f - (float)f.GutsTimer / _rules.GutsDelayFrames;
            var bar = new Rect2(body.Position.X - 6, body.End.Y + 6, body.Size.X + 12, 6);
            DrawRect(bar, new Color(Gold, 0.3f));
            DrawRect(new Rect2(bar.Position, new Vector2(bar.Size.X * filled, bar.Size.Y)), Gold);
        }

        // Centred over the body, with tags on their own line, so labels of fighters standing next
        // to each other don't run together.
        float labelY = body.Position.Y - 10;
        DrawCentred(font, $"P{i + 1}", body.GetCenter().X, labelY, 22, c);
        string? tag = f.ArmorFrames > 0 ? "GUTS!" : f.Action == FighterAction.Stunned ? "STUN" : IsCpu(i) ? "CPU" : null;
        if (tag != null) DrawCentred(font, tag, body.GetCenter().X, labelY - 22, 15, tag == "CPU" ? c : Gold);
    }

    private void DrawHud(Font font)
    {
        Vector2 size = GetViewportRect().Size;
        float slot = size.X / _state.Fighters.Length;

        for (int i = 0; i < _state.Fighters.Length; i++)
        {
            ref readonly Fighter f = ref _state.Fighters[i];
            Color c = PlayerColors[i % PlayerColors.Length];
            var pos = new Vector2(slot * i + 40, size.Y - 70);
            string status = f.Action == FighterAction.Eliminated ? "OUT" : $"{f.Josh}%";
            DrawString(font, pos, $"{PlayerLabel(i)}  {status}", HorizontalAlignment.Left, -1, 40, c);
            DrawString(font, pos + new Vector2(0, 34), new string('*', System.Math.Max(0, f.Stocks)) + "  stocks", HorizontalAlignment.Left, -1, 22, c);
            if (_rules.Guts)
            {
                string guts = f.GutsTimer > 0 ? "guts pending" : f.GutsUsed ? "guts used" : "guts ready";
                DrawString(font, pos + new Vector2(150, 34), guts, HorizontalAlignment.Left, -1, 18,
                    f.GutsUsed && f.GutsTimer == 0 ? new Color(c, 0.4f) : Gold);
            }
        }

        if (_state.MatchOver)
        {
            string text = _state.WinnerIndex >= 0 ? $"P{_state.WinnerIndex + 1} WINS!  (F5 to restart)" : "DRAW  (F5 to restart)";
            DrawString(font, new Vector2(0, size.Y * 0.3f), text, HorizontalAlignment.Center, size.X, 72, Colors.White);
        }

        for (int i = 0; i < _chatLog.Count; i++)
            DrawString(font, new Vector2(40, 60 + i * 28), _chatLog[i], HorizontalAlignment.Left, -1, 22, new Color(1, 1, 1, 0.85f));

        if (ShowDebug)
        {
            string dbg = _smoke != null
                ? $"SMOKE TEST (bots)   frame {_state.Frame}/{SmokeScript.Frames}   checksum {_state.Checksum():X16}"
                : $"frame {_state.Frame}   checksum {_state.Checksum():X16}   T = quick chat   " +
                  $"F2 = CPU {(CpuOpponents ? "on" : "off")}   F3 = CPU level: {_cpuLevel}   F4 = Guts {(_guts ? "on" : "off")}   F5 = restart";
            DrawString(font, new Vector2(40, size.Y - 110), dbg, HorizontalAlignment.Left, -1, 18, new Color(1, 1, 1, 0.5f));
        }
    }
}
