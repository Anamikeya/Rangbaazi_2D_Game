using System;

namespace Rangbaazi.Sim;

/// <summary>
/// One player's input for one simulation frame, packed into 16 bits so the netcode layer
/// (Backdash) can send it cheaply. Maps to the GDD B2 control scheme.
/// </summary>
[Flags]
public enum Buttons : ushort
{
    None = 0,
    Left = 1 << 0,
    Right = 1 << 1,
    Up = 1 << 2,
    Down = 1 << 3,
    Jump = 1 << 4,
    Light = 1 << 5,
    Heavy = 1 << 6,
    Dodge = 1 << 7,
    Grab = 1 << 8,
    DropWeapon = 1 << 9,
    Taunt = 1 << 10,
}
