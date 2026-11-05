using Rangbaazi.Social.Chat;

namespace Rangbaazi.Social.Tests;

public class ChatFilterTests
{
    // Placeholder words; real lists come from the backend / moderation vendor.
    private readonly ChatFilter _filter = new(new[] { "badword", "rude" });

    [Fact]
    public void MasksWholeWordsCaseInsensitively()
    {
        Assert.Equal("you ******* !", _filter.Apply("you BadWord !"));
    }

    [Fact]
    public void MasksBasicLeetspeak()
    {
        Assert.Equal("****", _filter.Apply("rud3"));
        Assert.Equal("*******", _filter.Apply("b@dw0rd"));
    }

    [Fact]
    public void DoesNotMaskInsideLongerWords()
    {
        // Avoid the "Scunthorpe problem": only whole tokens are matched.
        Assert.Equal("crude prudence", _filter.Apply("crude prudence"));
    }

    [Fact]
    public void StripsControlAndBidiCharactersAndFlattensNewlines()
    {
        Assert.Equal("hi there", ChatFilter.Sanitize("hi\nthere\u0007"));
        Assert.Equal("abc", ChatFilter.Sanitize("a‮b‬c"));
    }

    [Fact]
    public void KeepsDevanagariIntact()
    {
        const string hindi = "नमस्ते"; // नमस्ते (uses a virama mark)
        Assert.Equal(hindi, _filter.Apply(hindi));
    }

    [Fact]
    public void MasksBlockedDevanagariWordAsOneToken()
    {
        const string word = "गंदा"; // a word with combining marks
        var f = new ChatFilter(new[] { word });
        Assert.Equal("****", f.Apply(word));
    }

    [Fact]
    public void TruncatesToMaxLengthWithoutSplittingSurrogatePairs()
    {
        string s = new string('a', ChatFilter.MaxLength - 1) + "\U0001F600";
        string clean = ChatFilter.Sanitize(s);
        Assert.Equal(ChatFilter.MaxLength - 1, clean.Length);
    }
}
