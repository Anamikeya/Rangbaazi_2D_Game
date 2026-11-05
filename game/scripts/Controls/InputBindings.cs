using Godot;
using Rangbaazi.Sim;

namespace Rangbaazi.Game.Controls;

/// <summary>
/// Registers default bindings at startup (GDD B2 controls) and reads them into sim <see cref="Buttons"/>.
/// Defined in code instead of project.godot so remapping/saving can be added in one place later.
///
///   P1 keyboard: A/D move, W up, S down, Space jump, J light, K heavy, L dodge, I grab, U drop
///   P2 keyboard: arrows move, Up/Down aim, Numpad 0 jump, 1 light, 2 heavy, 3 dodge, 5 grab, 4 drop
///   Pads: device N drives player N+1. A jump, X light, Y heavy, RB dodge, B grab, LB drop
///   Chat/voice (online only): Enter open chat, T quick-chat wheel, V push-to-talk;
///     pad: View/Back quick-chat wheel, R3 push-to-talk
/// </summary>
public static class InputBindings
{
    public const int MaxLocalPlayers = 4;

    public static readonly StringName ChatOpen = "chat_open";
    public static readonly StringName QuickChatWheel = "quick_chat_wheel";
    public static readonly StringName PushToTalk = "push_to_talk";
    public static readonly StringName DebugRestart = "debug_restart";

    private static readonly (Buttons Button, string Suffix)[] Map =
    {
        (Buttons.Left, "left"), (Buttons.Right, "right"), (Buttons.Up, "up"), (Buttons.Down, "down"),
        (Buttons.Jump, "jump"), (Buttons.Light, "light"), (Buttons.Heavy, "heavy"), (Buttons.Dodge, "dodge"),
        (Buttons.Grab, "grab"), (Buttons.DropWeapon, "drop"),
    };

    /// <summary>Default keyboard keys for P1 and P2, in <see cref="Map"/> order.</summary>
    private static readonly Key[][] DefaultKeys =
    {
        new[] { Key.A, Key.D, Key.W, Key.S, Key.Space, Key.J, Key.K, Key.L, Key.I, Key.U },
        new[] { Key.Left, Key.Right, Key.Up, Key.Down, Key.Kp0, Key.Kp1, Key.Kp2, Key.Kp3, Key.Kp5, Key.Kp4 },
    };

    private static StringName[][]? _actions;

    /// <summary>The default keyboard key bound to one button for a player, if that player has keys.</summary>
    public static bool TryGetDefaultKey(int player, Buttons button, out Key key)
    {
        key = Key.None;
        if (player < 0 || player >= DefaultKeys.Length) return false;
        for (int i = 0; i < Map.Length; i++)
        {
            if (Map[i].Button != button) continue;
            key = DefaultKeys[player][i];
            return true;
        }
        return false;
    }

    public static void Register()
    {
        if (_actions != null) return;

        _actions = new StringName[MaxLocalPlayers][];
        for (int p = 0; p < MaxLocalPlayers; p++)
        {
            _actions[p] = new StringName[Map.Length];
            for (int i = 0; i < Map.Length; i++)
            {
                var name = new StringName($"p{p + 1}_{Map[i].Suffix}");
                _actions[p][i] = name;
                if (!InputMap.HasAction(name)) InputMap.AddAction(name, 0.4f);
            }
            AddPad(p);
        }

        for (int p = 0; p < DefaultKeys.Length; p++) AddKeys(p, DefaultKeys[p]);

        AddGlobal(ChatOpen, Key.Enter);
        AddGlobal(QuickChatWheel, Key.T, JoyButton.Back);
        AddGlobal(PushToTalk, Key.V, JoyButton.RightStick);
        AddGlobal(DebugRestart, Key.F5);
    }

    public static Buttons Read(int player)
    {
        if (_actions == null || player < 0 || player >= MaxLocalPlayers) return Buttons.None;
        Buttons b = Buttons.None;
        StringName[] actions = _actions[player];
        for (int i = 0; i < Map.Length; i++)
            if (Godot.Input.IsActionPressed(actions[i])) b |= Map[i].Button;
        return b;
    }

    private static void AddKeys(int player, Key[] keys)
    {
        for (int i = 0; i < keys.Length; i++)
            InputMap.ActionAddEvent(_actions![player][i], new InputEventKey { PhysicalKeycode = keys[i] });
    }

    private static void AddPad(int player)
    {
        StringName[] a = _actions![player];
        int device = player;
        void Button(int idx, JoyButton b) => InputMap.ActionAddEvent(a[idx], new InputEventJoypadButton { ButtonIndex = b, Device = device });
        void Axis(int idx, JoyAxis axis, float dir) => InputMap.ActionAddEvent(a[idx], new InputEventJoypadMotion { Axis = axis, AxisValue = dir, Device = device });

        Axis(0, JoyAxis.LeftX, -1f); Button(0, JoyButton.DpadLeft);
        Axis(1, JoyAxis.LeftX, 1f); Button(1, JoyButton.DpadRight);
        Axis(2, JoyAxis.LeftY, -1f); Button(2, JoyButton.DpadUp);
        Axis(3, JoyAxis.LeftY, 1f); Button(3, JoyButton.DpadDown);
        Button(4, JoyButton.A);
        Button(5, JoyButton.X);
        Button(6, JoyButton.Y);
        Button(7, JoyButton.RightShoulder);
        Button(8, JoyButton.B);
        Button(9, JoyButton.LeftShoulder);
    }

    private static void AddGlobal(StringName action, Key key, JoyButton? pad = null)
    {
        if (!InputMap.HasAction(action)) InputMap.AddAction(action);
        InputMap.ActionAddEvent(action, new InputEventKey { PhysicalKeycode = key });
        if (pad is JoyButton b) InputMap.ActionAddEvent(action, new InputEventJoypadButton { ButtonIndex = b, Device = -1 });
    }
}
