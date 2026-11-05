using System.Collections.Generic;
using System.Linq;

namespace Rangbaazi.Social.Chat;

public enum QuickChatCategory
{
    Callout,
    Social,
    Filmi,
}

/// <summary>
/// A pre-written line. Safe for every age band and every language: only the id travels over the
/// network and each client shows it in its own language. <paramref name="Gloss"/> is the English
/// meaning shown as a subtitle for Hinglish "filmi" lines.
/// </summary>
public sealed record QuickChatLine(string Id, QuickChatCategory Category, string LocKey, string DefaultText, string? Gloss = null);

/// <summary>
/// Launch quick-chat wheel ("Dialogue-baazi"). All lines are original: no real film dialogue,
/// song lyrics or actor catchphrases (GDD B8 cultural guardrails).
/// </summary>
public static class QuickChatCatalog
{
    public static IReadOnlyList<QuickChatLine> All { get; } = new[]
    {
        new QuickChatLine("callout.help", QuickChatCategory.Callout, "qc.callout.help", "Help!"),
        new QuickChatLine("callout.group_up", QuickChatCategory.Callout, "qc.callout.group_up", "Group up!"),
        new QuickChatLine("callout.prop_box", QuickChatCategory.Callout, "qc.callout.prop_box", "Prop Box here!"),
        new QuickChatLine("callout.taali_ready", QuickChatCategory.Callout, "qc.callout.taali_ready", "My super is ready!"),
        new QuickChatLine("callout.watch_out", QuickChatCategory.Callout, "qc.callout.watch_out", "Watch out!"),

        new QuickChatLine("social.gg", QuickChatCategory.Social, "qc.social.gg", "GG!"),
        new QuickChatLine("social.nice", QuickChatCategory.Social, "qc.social.nice", "Nice one!"),
        new QuickChatLine("social.thanks", QuickChatCategory.Social, "qc.social.thanks", "Thanks!"),
        new QuickChatLine("social.sorry", QuickChatCategory.Social, "qc.social.sorry", "Sorry!"),
        new QuickChatLine("social.rematch", QuickChatCategory.Social, "qc.social.rematch", "Rematch?"),

        new QuickChatLine("filmi.hero_late", QuickChatCategory.Filmi, "qc.filmi.hero_late", "Hero ki entry hamesha late hoti hai!", "The hero always arrives late!"),
        new QuickChatLine("filmi.interval", QuickChatCategory.Filmi, "qc.filmi.interval", "Interval ke baad milte hain.", "See you after the interval."),
        new QuickChatLine("filmi.trailer", QuickChatCategory.Filmi, "qc.filmi.trailer", "Trailer khatam, ab picture shuru!", "Trailer's over, now the movie starts!"),
        new QuickChatLine("filmi.taali", QuickChatCategory.Filmi, "qc.filmi.taali", "Taaliyan bajao!", "Let's hear that applause!"),
        new QuickChatLine("filmi.paisa_vasool", QuickChatCategory.Filmi, "qc.filmi.paisa_vasool", "Full paisa vasool!", "Worth every rupee of the ticket!"),
    };

    private static readonly Dictionary<string, QuickChatLine> ById = All.ToDictionary(l => l.Id);

    public static bool TryGet(string id, out QuickChatLine line) => ById.TryGetValue(id, out line!);
}
