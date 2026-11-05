using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Rangbaazi.Social.Chat;

/// <summary>
/// Client-side text cleanup: strips control characters, caps length, and masks blocked words
/// (whole-word, case-insensitive, basic leetspeak like "b4d" -> "bad"). Works with Devanagari
/// and other scripts that use combining marks.
///
/// This is a courtesy layer for instant feedback only. Authoritative filtering, moderation and
/// logging must run server-side in the chat backend. Word lists ship from the backend/moderation
/// vendor, never hard-coded here.
/// </summary>
public sealed class ChatFilter
{
    public const int MaxLength = 200;

    private readonly HashSet<string> _blocked;

    public ChatFilter(IEnumerable<string>? blockedWords = null)
    {
        _blocked = new HashSet<string>(StringComparer.Ordinal);
        if (blockedWords == null) return;
        foreach (string w in blockedWords)
        {
            string n = Normalize(w.Trim());
            if (n.Length > 0) _blocked.Add(n);
        }
    }

    public static ChatFilter None { get; } = new();

    public string Apply(string raw) => Mask(Sanitize(raw));

    /// <summary>Removes control/format characters, turns newlines and tabs into spaces, trims and truncates.</summary>
    public static string Sanitize(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (char c in raw)
        {
            if (c is '\n' or '\r' or '\t') { sb.Append(' '); continue; }
            UnicodeCategory cat = char.GetUnicodeCategory(c);
            if (cat is UnicodeCategory.Control) continue;
            // Drop bidi overrides and other format chars except ZWJ/ZWNJ, which Indic scripts need.
            if (cat is UnicodeCategory.Format && c != '‍' && c != '‌') continue;
            sb.Append(c);
        }

        string s = sb.ToString().Trim();
        if (s.Length <= MaxLength) return s;
        int cut = MaxLength;
        if (char.IsHighSurrogate(s[cut - 1])) cut--;
        return s.Substring(0, cut);
    }

    /// <summary>Replaces each blocked whole word with asterisks of the same length.</summary>
    public string Mask(string text)
    {
        if (_blocked.Count == 0 || text.Length == 0) return text;

        char[]? output = null;
        int i = 0;
        while (i < text.Length)
        {
            if (!IsWordChar(text[i])) { i++; continue; }
            int start = i;
            while (i < text.Length && IsWordChar(text[i])) i++;

            string token = Normalize(text.AsSpan(start, i - start));
            if (_blocked.Contains(token))
            {
                output ??= text.ToCharArray();
                for (int k = start; k < i; k++) output[k] = '*';
            }
        }
        return output == null ? text : new string(output);
    }

    private static bool IsWordChar(char c)
    {
        if (char.IsLetterOrDigit(c) || c is '@' or '$') return true;
        UnicodeCategory cat = char.GetUnicodeCategory(c);
        return cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;
    }

    private static string Normalize(ReadOnlySpan<char> word)
    {
        var sb = new StringBuilder(word.Length);
        foreach (char c in word)
        {
            sb.Append(char.ToLowerInvariant(c) switch
            {
                '0' => 'o',
                '1' => 'i',
                '3' => 'e',
                '4' => 'a',
                '5' => 's',
                '7' => 't',
                '@' => 'a',
                '$' => 's',
                var other => other,
            });
        }
        return sb.ToString();
    }
}
