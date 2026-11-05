using Rangbaazi.Social.Safety;
using Rangbaazi.Social.Voice;

namespace Rangbaazi.Social.Tests;

public class VoiceControllerTests
{
    private static readonly VoiceChannelId Party = new(VoiceChannelKind.Party, "p1");
    private static readonly VoiceChannelId Team = new(VoiceChannelKind.Team, "t1");
    private static readonly VoiceChannelId MatchWide = new(VoiceChannelKind.Match, "m1");
    private static readonly PlayerId Troll = new("troll");

    private static (VoiceController c, FakeVoiceService s, BlockList blocks) Create(SocialPolicy policy, VoiceSettings? settings = null)
    {
        var s = new FakeVoiceService();
        var blocks = new BlockList();
        return (new VoiceController(s, policy, blocks, settings), s, blocks);
    }

    [Fact]
    public async Task PolicyGatesChannelKinds()
    {
        var (c, s, _) = Create(SocialPolicy.Default(AgeBand.Adult)); // PartyAndTeam
        Assert.True(await c.TryJoinAsync(Party));
        Assert.True(await c.TryJoinAsync(Team));
        Assert.False(await c.TryJoinAsync(MatchWide));
        Assert.Equal(2, s.Joined.Count);
    }

    [Fact]
    public async Task ChildAccountsCannotJoinAnyVoice()
    {
        var (c, s, _) = Create(SocialPolicy.Default(AgeBand.Under13));
        Assert.False(await c.TryJoinAsync(Party));
        Assert.Empty(s.Joined);
    }

    [Fact]
    public async Task PushToTalkTransmitsOnlyWhileHeld()
    {
        var (c, s, _) = Create(SocialPolicy.Default(AgeBand.Adult));
        await c.TryJoinAsync(Party);
        Assert.False(s.Transmitting);

        c.SetPushToTalk(true);
        Assert.True(s.Transmitting);
        c.SetPushToTalk(false);
        Assert.False(s.Transmitting);
    }

    [Fact]
    public void PushToTalkDoesNothingOutsideAChannel()
    {
        var (c, s, _) = Create(SocialPolicy.Default(AgeBand.Adult));
        c.SetPushToTalk(true);
        Assert.False(s.Transmitting);
        Assert.Equal(0, s.TransmitChanges);
    }

    [Fact]
    public async Task VoiceActivatedTransmitsWhileInChannel()
    {
        var (c, s, _) = Create(SocialPolicy.Default(AgeBand.Adult), new VoiceSettings { Mode = TransmitMode.VoiceActivated });
        await c.TryJoinAsync(Team);
        Assert.True(s.Transmitting);
        await c.LeaveAsync(Team);
        Assert.False(s.Transmitting);
    }

    [Fact]
    public async Task BlockingMutesImmediatelyAndOnLaterJoins()
    {
        var (c, s, blocks) = Create(SocialPolicy.Default(AgeBand.Adult));
        await c.TryJoinAsync(Team);

        blocks.Block(Troll);
        Assert.True(s.Muted[Troll]);

        s.Muted.Clear();
        s.SimulateJoin(Team, Troll);
        Assert.True(s.Muted[Troll]);

        blocks.Unblock(Troll);
        Assert.False(s.Muted[Troll]);
    }

    [Fact]
    public async Task UnblockKeepsLocalMute()
    {
        var (c, s, blocks) = Create(SocialPolicy.Default(AgeBand.Adult));
        await c.TryJoinAsync(Team);
        c.SetPlayerMuted(Troll, true);
        blocks.Block(Troll);
        blocks.Unblock(Troll);
        Assert.True(s.Muted[Troll]);
    }

    [Fact]
    public async Task TighteningPolicyLeavesDisallowedChannels()
    {
        var (c, s, _) = Create(SocialPolicy.Default(AgeBand.Adult));
        await c.TryJoinAsync(Party);
        await c.TryJoinAsync(Team);

        await c.SetPolicyAsync(new SocialPolicy(TextChatScope.Everyone, VoiceChatScope.PartyOnly));
        Assert.Equal(new[] { Party }, s.Joined.ToArray());
    }

    [Fact]
    public void SettingsAreClamped()
    {
        var (_, s, _) = Create(SocialPolicy.Default(AgeBand.Adult), new VoiceSettings { InputVolume = 900 });
        Assert.Equal(200, s.InputVolume);
    }
}
