using Rangbaazi.Social.Chat;
using Rangbaazi.Social.Safety;

namespace Rangbaazi.Social.Tests;

public class ChatServiceTests
{
    private static readonly ChatChannelId Lobby = new(ChatChannelKind.Lobby, "room-1");
    private static readonly PlayerId Asha = new("asha");
    private static readonly PlayerId Ben = new("ben");

    private readonly ManualTimeProvider _time = new();

    private (LocalChatHub hub, IChatService a, IChatService b, List<ChatMessage> bInbox) Setup(
        SocialPolicy? bPolicy = null, Relationship aToB = Relationship.Stranger)
    {
        var hub = new LocalChatHub(new ChatFilter(new[] { "badword" }), _time);
        var a = hub.Connect(Asha, "Asha", SocialPolicy.Default(AgeBand.Adult));
        var b = hub.Connect(Ben, "Ben", bPolicy ?? SocialPolicy.Default(AgeBand.Adult), _ => aToB);
        var inbox = new List<ChatMessage>();
        b.MessageReceived += inbox.Add;
        a.JoinAsync(Lobby).Wait();
        b.JoinAsync(Lobby).Wait();
        return (hub, a, b, inbox);
    }

    [Fact]
    public async Task TextIsDeliveredAndServerFiltered()
    {
        var (_, a, _, inbox) = Setup();
        Assert.Equal(ChatSendResult.Sent, await a.SendTextAsync(Lobby, "gg badword"));
        Assert.Equal("gg *******", Assert.Single(inbox).Text);
    }

    [Fact]
    public async Task MustJoinChannelBeforeSending()
    {
        var (_, a, _, _) = Setup();
        var other = new ChatChannelId(ChatChannelKind.Team, "t2");
        Assert.Equal(ChatSendResult.NotInChannel, await a.SendTextAsync(other, "hi"));
    }

    [Fact]
    public async Task BlockedPlayersAreHidden()
    {
        var (_, a, b, inbox) = Setup();
        b.Blocks.Block(Asha);
        await a.SendTextAsync(Lobby, "hello");
        await a.SendQuickChatAsync(Lobby, "social.gg");
        Assert.Empty(inbox);
    }

    [Fact]
    public async Task QuickChatOnlyPolicyCanSendLinesButNotFreeText()
    {
        var hub = new LocalChatHub(time: _time);
        var kid = hub.Connect(new PlayerId("kid"), "Kid", SocialPolicy.Default(AgeBand.Under13));
        await kid.JoinAsync(Lobby);

        Assert.Equal(ChatSendResult.BlockedByPolicy, await kid.SendTextAsync(Lobby, "hello"));
        Assert.Equal(ChatSendResult.Sent, await kid.SendQuickChatAsync(Lobby, "social.gg"));
    }

    [Fact]
    public async Task QuickChatOnlyPolicyHidesOthersFreeTextButShowsLines()
    {
        var (_, a, _, inbox) = Setup(bPolicy: SocialPolicy.Default(AgeBand.Under13));
        await a.SendTextAsync(Lobby, "hello");
        await a.SendQuickChatAsync(Lobby, "filmi.hero_late");

        ChatMessage only = Assert.Single(inbox);
        Assert.Equal(ChatContentKind.QuickChat, only.Kind);
        Assert.Equal("filmi.hero_late", only.QuickChatId);
    }

    [Fact]
    public async Task FriendsOnlyShowsFreeTextOnlyFromFriends()
    {
        var friendsOnly = new SocialPolicy(TextChatScope.FriendsOnly, VoiceChatScope.Off);

        var (_, a1, _, strangerInbox) = Setup(friendsOnly, Relationship.Stranger);
        await a1.SendTextAsync(Lobby, "hi");
        Assert.Empty(strangerInbox);

        var (_, a2, _, friendInbox) = Setup(friendsOnly, Relationship.Friend);
        await a2.SendTextAsync(Lobby, "hi");
        Assert.Single(friendInbox);
    }

    [Fact]
    public async Task RateLimitAllowsBurstThenRefills()
    {
        var (_, a, _, _) = Setup();
        for (int i = 0; i < 5; i++) Assert.Equal(ChatSendResult.Sent, await a.SendTextAsync(Lobby, $"m{i}"));
        Assert.Equal(ChatSendResult.RateLimited, await a.SendTextAsync(Lobby, "spam"));

        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(ChatSendResult.Sent, await a.SendTextAsync(Lobby, "ok now"));
    }

    [Fact]
    public async Task UnknownQuickChatIdIsRejected()
    {
        var (_, a, _, _) = Setup();
        Assert.Equal(ChatSendResult.UnknownQuickChat, await a.SendQuickChatAsync(Lobby, "made.up"));
    }

    [Fact]
    public async Task EmptyMessagesAreRejected()
    {
        var (_, a, _, _) = Setup();
        Assert.Equal(ChatSendResult.Empty, await a.SendTextAsync(Lobby, " \n\t "));
    }

    [Fact]
    public void SystemMessagesAlwaysArrive()
    {
        var (hub, _, _, inbox) = Setup(bPolicy: new SocialPolicy(TextChatScope.Off, VoiceChatScope.Off));
        hub.BroadcastSystem(ChatChannelId.System, "Server restart in 5 min");
        Assert.Equal(ChatContentKind.System, Assert.Single(inbox).Kind);
    }

    [Fact]
    public async Task ReportsAreRecorded()
    {
        var (hub, _, b, _) = Setup();
        await b.ReportAsync(Asha, ReportReason.Harassment, "lobby");
        ChatReport r = Assert.Single(hub.Reports);
        Assert.Equal(Ben, r.Reporter);
        Assert.Equal(Asha, r.Target);
    }

    [Fact]
    public void QuickChatCatalogIdsAreUnique()
    {
        var ids = QuickChatCatalog.All.Select(l => l.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(QuickChatCatalog.All.Where(l => l.Category == QuickChatCategory.Filmi), l => Assert.NotNull(l.Gloss));
    }
}
