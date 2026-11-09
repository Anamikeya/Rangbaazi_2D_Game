using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Rangbaazi.Sim;
using Rangbaazi.Sim.Diagnostics;

namespace Rangbaazi.Net;

/// <summary>
/// A 64-bit summary of everything that must be equal on both peers for their simulations to stay
/// in step: the netcode protocol version, every value in the <see cref="MatchRules"/> (stage,
/// tuning, frame data), and the sim's behaviour, taken as the checksum of a short scripted bot
/// fight under those rules. The behaviour part catches two builds whose sim code differs.
/// Peers exchange it in their hello and refuse to play if it differs, instead of desyncing later.
/// </summary>
public static class MatchFingerprint
{
    /// <summary>Frames of the scripted fight. Long enough to include jumps, hits and dodges.</summary>
    internal const int BehaviourFrames = 600;

    public static ulong Compute(MatchRules rules)
    {
        var h = new StateHasher();
        h.Add(NetProtocol.Version);
        h.Add(RulesHash(rules));
        h.Add(BehaviourChecksum(rules));
        return h.Value;
    }

    /// <summary>Hash of every field reachable from the rules (properties' backing fields included).</summary>
    internal static ulong RulesHash(MatchRules rules)
    {
        var h = new StateHasher();
        var visiting = new HashSet<object>(ReferenceEqualityComparer.Instance);
        Walk(rules, ref h, visiting);
        return h.Value;
    }

    /// <summary>Checksum after two sparring bots fight for a few seconds under these rules.</summary>
    internal static ulong BehaviourChecksum(MatchRules rules)
    {
        GameState state = Simulation.CreateInitialState(rules, 2, seed: 0x5EED);
        var inputs = new Buttons[2];
        for (int f = 0; f < BehaviourFrames; f++)
        {
            for (int p = 0; p < inputs.Length; p++) inputs[p] = SparringBot.Think(state, rules, p, CpuLevel.Hard);
            Simulation.Advance(state, rules, inputs);
        }
        return state.Checksum();
    }

    private static void Walk(object? value, ref StateHasher h, HashSet<object> visiting)
    {
        switch (value)
        {
            case null:
                h.Add(0xA11CEUL);
                return;
            case bool b:
                h.Add(b);
                return;
            case string s:
                AddString(s, ref h);
                return;
            case Enum e:
                h.Add(Convert.ToInt64(e, System.Globalization.CultureInfo.InvariantCulture));
                return;
            case byte or sbyte or short or ushort or int or uint or long or char:
                h.Add(Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
                return;
            case ulong u:
                h.Add(u);
                return;
            case float or double or decimal:
                throw new NotSupportedException("Match rules must not contain floating point values.");
            case Delegate:
                throw new NotSupportedException("Match rules must not contain delegates.");
            case IEnumerable items:
            {
                int count = 0;
                foreach (object? item in items)
                {
                    Walk(item, ref h, visiting);
                    count++;
                }
                h.Add(count);
                return;
            }
        }

        Type type = value.GetType();
        if (!type.IsValueType && !visiting.Add(value)) throw new NotSupportedException($"Match rules contain a cycle through {type.Name}.");
        AddString(type.FullName ?? type.Name, ref h);
        foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                     .OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            AddString(field.Name, ref h);
            Walk(field.GetValue(value), ref h, visiting);
        }
        if (!type.IsValueType) visiting.Remove(value);
    }

    private static void AddString(string s, ref StateHasher h)
    {
        h.Add(s.Length);
        foreach (byte b in Encoding.UTF8.GetBytes(s)) h.Add((int)b);
    }
}
