using Rangbaazi.Social.Safety;

namespace Rangbaazi.Social.Tests;

public class SocialPolicyTests
{
    [Theory]
    [InlineData(AgeBand.Unknown)]
    [InlineData(AgeBand.Under13)]
    public void ChildrenAndUnknownAgeGetQuickChatOnlyAndNoVoice(AgeBand band)
    {
        var p = SocialPolicy.For(band, TextChatScope.Everyone, VoiceChatScope.Everyone);
        Assert.Equal(TextChatScope.QuickChatOnly, p.Text);
        Assert.Equal(VoiceChatScope.Off, p.Voice);
    }

    [Fact]
    public void TeensCanOptUpToTeamVoiceButNotMatchWide()
    {
        var p = SocialPolicy.For(AgeBand.Teen13To17, TextChatScope.Everyone, VoiceChatScope.Everyone);
        Assert.Equal(TextChatScope.Everyone, p.Text);
        Assert.Equal(VoiceChatScope.PartyAndTeam, p.Voice);
    }

    [Fact]
    public void AdultsDefaultToPartyAndTeamVoice()
    {
        Assert.Equal(VoiceChatScope.PartyAndTeam, SocialPolicy.For(AgeBand.Adult).Voice);
    }

    [Fact]
    public void PlayersCanAlwaysChooseLessThanTheirCeiling()
    {
        var p = SocialPolicy.For(AgeBand.Adult, TextChatScope.Off, VoiceChatScope.Off);
        Assert.Equal(TextChatScope.Off, p.Text);
        Assert.Equal(VoiceChatScope.Off, p.Voice);
    }
}
