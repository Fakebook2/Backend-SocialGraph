namespace SocialGraph.Api.Tests;

using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using SocialGraph.Api.Database;
using SocialGraph.Api.Service;

public sealed class CandidateServiceTests
{
    private const long UserId = 100;

    [Fact]
    public async Task PostCandidateIds_CombinesSocialAndPublicSources_WithPrivacyAndBlockFiltering()
    {
        await using var context = CreateContext();
        context.ObjectsTb.AddRange(
            User(200), User(201), User(202), User(203), User(204),
            Group(300, privacy: 1), Group(301, privacy: 0), Group(302, privacy: 0), Group(303, privacy: 1),
            Post(1_000, GraphObjectType.FeedPost, privacy: 1),
            Post(1_001, GraphObjectType.FeedPost, privacy: 1),
            Post(1_002, GraphObjectType.FeedPost, privacy: 0),
            Post(1_003, GraphObjectType.GroupPost, privacy: 0),
            Post(1_004, GraphObjectType.GroupPost, privacy: 0),
            Post(1_005, GraphObjectType.FeedPost, privacy: 0),
            Post(1_006, GraphObjectType.Reel, privacy: 2),
            Post(1_007, GraphObjectType.Reel, privacy: 1),
            Post(1_008, GraphObjectType.Reel, privacy: 0),
            Post(1_009, GraphObjectType.Reel, privacy: 2),
            Post(1_010, GraphObjectType.Reel, privacy: 0),
            Post(1_011, GraphObjectType.GroupPost, privacy: 3),
            Post(1_012, GraphObjectType.GroupPost, privacy: 0));
        context.AssociationsTb.AddRange(
            Edge(UserId, GraphAssociationType.Friend, 200),
            Edge(UserId, GraphAssociationType.Followed, 201),
            Edge(UserId, GraphAssociationType.Member, 300),
            Edge(UserId, GraphAssociationType.Blocked, 203),
            Authored(200, 1_000), Authored(201, 1_001), Authored(201, 1_002),
            Authored(202, 1_003), Authored(203, 1_004), Authored(204, 1_005),
            Authored(200, 1_006), Authored(201, 1_007), Authored(204, 1_008),
            Authored(201, 1_009), Authored(203, 1_010), Authored(204, 1_011), Authored(204, 1_012),
            AuthoredBy(1_000, 200), AuthoredBy(1_001, 201), AuthoredBy(1_002, 201),
            AuthoredBy(1_003, 202), AuthoredBy(1_004, 203), AuthoredBy(1_005, 204),
            AuthoredBy(1_006, 200), AuthoredBy(1_007, 201), AuthoredBy(1_008, 204),
            AuthoredBy(1_009, 201), AuthoredBy(1_010, 203), AuthoredBy(1_011, 204), AuthoredBy(1_012, 204),
            Edge(300, GraphAssociationType.Published, 1_003),
            Edge(301, GraphAssociationType.Published, 1_004),
            Edge(302, GraphAssociationType.Published, 1_011),
            Edge(303, GraphAssociationType.Published, 1_012));
        await context.SaveChangesAsync();
        var service = new CandidateService(context);

        var ids = await service.GetPostCandidateIdsAsync(UserId, 20);

        Assert.Equal(new long[] { 1_006, 1_007, 1_003, 1_008, 1_011, 1_000, 1_002, 1_005, 1_001 }, ids);
        Assert.DoesNotContain(1_004, ids);
        Assert.DoesNotContain(1_009, ids);
        Assert.DoesNotContain(1_010, ids);
        Assert.DoesNotContain(1_012, ids);
    }

    [Fact]
    public async Task PostCandidateIds_DoesNotTruncateBlockListAtAssociationPageSize()
    {
        await using var context = CreateContext();
        const long blockedAuthorId = 999;
        const long blockedPostId = 2_000;
        context.ObjectsTb.AddRange(User(blockedAuthorId), Post(blockedPostId, GraphObjectType.FeedPost, privacy: 0));
        context.AssociationsTb.AddRange(
            Enumerable.Range(1, 101)
                .Select(index => Edge(UserId, GraphAssociationType.Blocked, 10_000 + index)));
        context.AssociationsTb.AddRange(
            Edge(UserId, GraphAssociationType.Blocked, blockedAuthorId),
            Authored(blockedAuthorId, blockedPostId),
            AuthoredBy(blockedPostId, blockedAuthorId));
        await context.SaveChangesAsync();
        var service = new CandidateService(context);

        var ids = await service.GetPostCandidateIdsAsync(UserId, 20);

        Assert.DoesNotContain(blockedPostId, ids);
    }

    [Fact]
    public async Task PostCandidateIds_SourceBalancedMerge_PreventsPublicCrowdOut()
    {
        await using var context = CreateContext();
        const long friendId = 200;
        const long publicAuthorId = 201;
        const long friendPostId = 1_000;
        context.ObjectsTb.AddRange(
            User(friendId),
            User(publicAuthorId),
            Post(friendPostId, GraphObjectType.FeedPost, privacy: 2));
        context.AssociationsTb.AddRange(
            Edge(UserId, GraphAssociationType.Friend, friendId),
            Authored(friendId, friendPostId),
            AuthoredBy(friendPostId, friendId));
        foreach (var postId in Enumerable.Range(2_000, 80).Select(value => (long)value))
        {
            context.ObjectsTb.Add(Post(postId, GraphObjectType.FeedPost, privacy: 0));
            context.AssociationsTb.Add(Authored(publicAuthorId, postId));
            context.AssociationsTb.Add(AuthoredBy(postId, publicAuthorId));
        }
        await context.SaveChangesAsync();

        var service = new CandidateService(context);
        var ids = await service.GetPostCandidateIdsAsync(UserId, 10);

        Assert.Equal(10, ids.Count);
        Assert.Contains(friendPostId, ids);
        Assert.Equal(friendPostId, ids[0]);
    }

    [Fact]
    public async Task ReelCandidates_SourceBalancedMerge_PreventsPublicCrowdOut()
    {
        await using var context = CreateContext();
        const long friendId = 200;
        const long publicAuthorId = 201;
        const long friendReelId = 1_000;
        context.ObjectsTb.AddRange(
            User(friendId),
            User(publicAuthorId),
            Post(friendReelId, GraphObjectType.Reel, privacy: 2));
        context.AssociationsTb.AddRange(
            Edge(UserId, GraphAssociationType.Friend, friendId),
            Authored(friendId, friendReelId),
            AuthoredBy(friendReelId, friendId));
        foreach (var reelId in Enumerable.Range(2_000, 80).Select(value => (long)value))
        {
            context.ObjectsTb.Add(Post(reelId, GraphObjectType.Reel, privacy: 0));
            context.AssociationsTb.Add(Authored(publicAuthorId, reelId));
            context.AssociationsTb.Add(AuthoredBy(reelId, publicAuthorId));
        }
        await context.SaveChangesAsync();

        var service = new CandidateService(context);
        var candidates = await service.GetReelCandidatesAsync(UserId, 10);

        Assert.Equal(10, candidates.Count);
        var friend = Assert.Single(candidates, item => item.Id == friendReelId);
        Assert.Equal("friend", friend.Source);
        Assert.Equal(friendReelId, candidates[0].Id);
    }

    [Fact]
    public async Task PostCandidates_ReturnPolicyFilteredMetadataWithoutContentOrMedia()
    {
        await using var context = CreateContext();
        context.ObjectsTb.AddRange(
            User(200),
            Group(300, privacy: 0),
            Post(4_000, GraphObjectType.FeedPost, privacy: 0),
            Post(4_001, GraphObjectType.GroupPost, privacy: 0));
        context.AssociationsTb.AddRange(
            Edge(UserId, GraphAssociationType.Friend, 200),
            Edge(UserId, GraphAssociationType.Member, 300),
            Authored(200, 4_000),
            AuthoredBy(4_000, 200),
            Authored(200, 4_001),
            AuthoredBy(4_001, 200),
            Edge(300, GraphAssociationType.Published, 4_001));
        await context.SaveChangesAsync();

        var service = new CandidateService(context);
        var candidates = await service.GetPostCandidatesAsync(UserId, 20);

        Assert.Equal(2, candidates.Count);
        var feed = Assert.Single(candidates, item => item.Id == 4_000);
        Assert.Equal(200, feed.AuthorId);
        Assert.Equal(GraphObjectType.FeedPost, feed.ContentType);
        Assert.Equal("friend", feed.Source);
        Assert.Null(feed.GroupId);
        Assert.NotEqual(string.Empty, feed.CreatedAt);

        var group = Assert.Single(candidates, item => item.Id == 4_001);
        Assert.Equal(GraphObjectType.GroupPost, group.ContentType);
        Assert.Equal(300, group.GroupId);
        Assert.Equal("group_member", group.Source);
    }

    [Fact]
    public async Task PostCandidates_OverfetchesBeforePrivacyQuotaSoVisibleOlderContentSurvives()
    {
        await using var context = CreateContext();
        const long friendId = 200;
        const long visibleOlderPostId = 5_000;
        context.ObjectsTb.Add(User(friendId));
        context.AssociationsTb.Add(Edge(UserId, GraphAssociationType.Friend, friendId));
        context.ObjectsTb.Add(Post(visibleOlderPostId, GraphObjectType.FeedPost, privacy: 2));
        context.AssociationsTb.AddRange(
            Authored(friendId, visibleOlderPostId),
            AuthoredBy(visibleOlderPostId, friendId));
        foreach (var postId in Enumerable.Range(5_001, 4).Select(value => (long)value))
        {
            context.ObjectsTb.Add(Post(postId, GraphObjectType.FeedPost, privacy: 3));
            context.AssociationsTb.AddRange(Authored(friendId, postId), AuthoredBy(postId, friendId));
        }

        await context.SaveChangesAsync();
        var service = new CandidateService(context);

        var candidates = await service.GetPostCandidatesAsync(UserId, 2);

        var visible = Assert.Single(candidates);
        Assert.Equal(visibleOlderPostId, visible.Id);
        Assert.Equal("friend", visible.Source);
    }

    [Fact]
    public async Task PostCandidates_IncludePrivateSelfContentButDoNotBypassPrivateGroupMembership()
    {
        await using var context = CreateContext();
        const long selfFeedId = 6_000;
        const long selfReelId = 6_001;
        const long allowedGroupPostId = 6_002;
        const long forbiddenGroupPostId = 6_003;
        const long memberGroupId = 700;
        const long otherPrivateGroupId = 701;
        context.ObjectsTb.AddRange(
            Group(memberGroupId, privacy: 1),
            Group(otherPrivateGroupId, privacy: 1),
            Post(selfFeedId, GraphObjectType.FeedPost, privacy: 3),
            Post(selfReelId, GraphObjectType.Reel, privacy: 3),
            Post(allowedGroupPostId, GraphObjectType.GroupPost, privacy: 0),
            Post(forbiddenGroupPostId, GraphObjectType.GroupPost, privacy: 0));
        context.AssociationsTb.AddRange(
            Edge(UserId, GraphAssociationType.Member, memberGroupId),
            Authored(UserId, selfFeedId), AuthoredBy(selfFeedId, UserId),
            Authored(UserId, selfReelId), AuthoredBy(selfReelId, UserId),
            Authored(UserId, allowedGroupPostId), AuthoredBy(allowedGroupPostId, UserId),
            Authored(UserId, forbiddenGroupPostId), AuthoredBy(forbiddenGroupPostId, UserId),
            Edge(memberGroupId, GraphAssociationType.Published, allowedGroupPostId),
            Edge(otherPrivateGroupId, GraphAssociationType.Published, forbiddenGroupPostId));
        await context.SaveChangesAsync();
        var service = new CandidateService(context);

        var candidates = await service.GetPostCandidatesAsync(UserId, 20);

        Assert.Contains(candidates, item => item.Id == selfFeedId && item.Source == "self");
        Assert.Contains(candidates, item => item.Id == selfReelId && item.Source == "self");
        Assert.Contains(candidates, item => item.Id == allowedGroupPostId && item.Source == "self");
        Assert.DoesNotContain(candidates, item => item.Id == forbiddenGroupPostId);
    }

    [Fact]
    public async Task PostCandidates_RoundRobinAuthorsPreventsAProlificFriendFromCrowdingOutAnother()
    {
        await using var context = CreateContext();
        const long prolificFriendId = 200;
        const long secondFriendId = 201;
        const long secondFriendPostId = 7_000;
        context.ObjectsTb.AddRange(User(prolificFriendId), User(secondFriendId));
        context.AssociationsTb.AddRange(
            Edge(UserId, GraphAssociationType.Friend, prolificFriendId),
            Edge(UserId, GraphAssociationType.Friend, secondFriendId));
        context.ObjectsTb.Add(Post(secondFriendPostId, GraphObjectType.FeedPost, privacy: 2));
        context.AssociationsTb.AddRange(
            Authored(secondFriendId, secondFriendPostId),
            AuthoredBy(secondFriendPostId, secondFriendId));
        foreach (var postId in Enumerable.Range(8_000, 30).Select(value => (long)value))
        {
            context.ObjectsTb.Add(Post(postId, GraphObjectType.FeedPost, privacy: 2));
            context.AssociationsTb.AddRange(
                Authored(prolificFriendId, postId),
                AuthoredBy(postId, prolificFriendId));
        }

        await context.SaveChangesAsync();
        var service = new CandidateService(context);

        var candidates = await service.GetPostCandidatesAsync(UserId, 10);

        Assert.Equal(10, candidates.Count);
        Assert.Contains(candidates, item => item.Id == secondFriendPostId);
        Assert.True(candidates.Select(item => item.AuthorId).Distinct().Count() >= 2);
    }

    [Fact]
    public async Task ReelCandidates_FollowingModeFillsFromRelationshipSourcesWithoutDiscoveryLeakage()
    {
        await using var context = CreateContext();
        const long friendId = 200;
        const long followedId = 201;
        const long publicAuthorId = 202;
        context.ObjectsTb.AddRange(User(friendId), User(followedId), User(publicAuthorId));
        context.AssociationsTb.AddRange(
            Edge(UserId, GraphAssociationType.Friend, friendId),
            Edge(UserId, GraphAssociationType.Followed, followedId));
        foreach (var reelId in Enumerable.Range(9_000, 20).Select(value => (long)value))
        {
            context.ObjectsTb.Add(Post(reelId, GraphObjectType.Reel, privacy: 2));
            context.AssociationsTb.AddRange(Authored(friendId, reelId), AuthoredBy(reelId, friendId));
        }

        foreach (var reelId in Enumerable.Range(10_000, 20).Select(value => (long)value))
        {
            context.ObjectsTb.Add(Post(reelId, GraphObjectType.Reel, privacy: 1));
            context.AssociationsTb.AddRange(Authored(followedId, reelId), AuthoredBy(reelId, followedId));
        }

        foreach (var reelId in Enumerable.Range(11_000, 40).Select(value => (long)value))
        {
            context.ObjectsTb.Add(Post(reelId, GraphObjectType.Reel, privacy: 0));
            context.AssociationsTb.AddRange(Authored(publicAuthorId, reelId), AuthoredBy(reelId, publicAuthorId));
        }

        await context.SaveChangesAsync();
        var service = new CandidateService(context);

        var candidates = await service.GetReelCandidatesAsync(UserId, 30, "FOLLOWING");

        Assert.Equal(30, candidates.Count);
        Assert.All(candidates, item => Assert.Contains(item.Source, new[] { "friend", "followed" }));
        Assert.DoesNotContain(candidates, item => item.AuthorId == publicAuthorId);
    }

    [Fact]
    public async Task ReelCandidates_RejectUnknownMode()
    {
        await using var context = CreateContext();
        var service = new CandidateService(context);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.GetReelCandidatesAsync(UserId, 20, "POPULAR"));
    }

    [Fact]
    public async Task PostCandidates_FilterPolicyBeforeSourceCapsSoInvalidBurstsDoNotStarveOlderVisibleRows()
    {
        await using var context = CreateContext();
        const long blockedAuthorId = 200;
        const long memberAuthorId = 201;
        const long publicAuthorId = 202;
        const long privateGroupAuthorId = 203;
        const long memberGroupId = 300;
        const long publicGroupId = 301;
        const long privateGroupId = 302;
        const long visibleFeedId = 1_200;
        const long visibleMemberGroupPostId = 1_201;
        const long visiblePublicGroupPostId = 1_202;
        context.ObjectsTb.AddRange(
            User(blockedAuthorId), User(memberAuthorId), User(publicAuthorId), User(privateGroupAuthorId),
            Group(memberGroupId, privacy: 1),
            Group(publicGroupId, privacy: 0),
            Group(privateGroupId, privacy: 1),
            Post(visibleFeedId, GraphObjectType.FeedPost, privacy: 0),
            Post(visibleMemberGroupPostId, GraphObjectType.GroupPost, privacy: 0),
            Post(visiblePublicGroupPostId, GraphObjectType.GroupPost, privacy: 0));
        context.AssociationsTb.AddRange(
            Edge(UserId, GraphAssociationType.Member, memberGroupId),
            Edge(UserId, GraphAssociationType.Blocked, blockedAuthorId),
            Authored(publicAuthorId, visibleFeedId), AuthoredBy(visibleFeedId, publicAuthorId),
            Authored(memberAuthorId, visibleMemberGroupPostId), AuthoredBy(visibleMemberGroupPostId, memberAuthorId),
            Edge(memberGroupId, GraphAssociationType.Published, visibleMemberGroupPostId),
            Authored(publicAuthorId, visiblePublicGroupPostId), AuthoredBy(visiblePublicGroupPostId, publicAuthorId),
            Edge(publicGroupId, GraphAssociationType.Published, visiblePublicGroupPostId));

        foreach (var postId in Enumerable.Range(2_000, 40).Select(value => (long)value))
        {
            context.ObjectsTb.Add(Post(postId, GraphObjectType.FeedPost, privacy: 0));
            context.AssociationsTb.AddRange(Authored(blockedAuthorId, postId), AuthoredBy(postId, blockedAuthorId));
        }

        foreach (var postId in Enumerable.Range(3_000, 40).Select(value => (long)value))
        {
            context.ObjectsTb.Add(Post(postId, GraphObjectType.GroupPost, privacy: 0));
            context.AssociationsTb.AddRange(
                Authored(blockedAuthorId, postId),
                AuthoredBy(postId, blockedAuthorId),
                Edge(memberGroupId, GraphAssociationType.Published, postId));
        }

        foreach (var postId in Enumerable.Range(4_000, 70).Select(value => (long)value))
        {
            context.ObjectsTb.Add(Post(postId, GraphObjectType.GroupPost, privacy: 0));
            context.AssociationsTb.AddRange(
                Authored(privateGroupAuthorId, postId),
                AuthoredBy(postId, privateGroupAuthorId),
                Edge(privateGroupId, GraphAssociationType.Published, postId));
        }

        await context.SaveChangesAsync();
        var service = new CandidateService(context);

        var candidates = await service.GetPostCandidatesAsync(UserId, 10);

        Assert.Contains(candidates, item => item.Id == visibleFeedId);
        Assert.Contains(candidates, item => item.Id == visibleMemberGroupPostId);
        Assert.Contains(candidates, item => item.Id == visiblePublicGroupPostId);
        Assert.DoesNotContain(candidates, item => item.AuthorId == blockedAuthorId);
        Assert.DoesNotContain(candidates, item => item.GroupId == privateGroupId);
    }

    private static MyDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MyDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new MyDbContext(options);
    }

    private static Objects User(long id) => new()
    {
        id = id,
        otype = GraphObjectType.User,
        data = "{}"
    };

    private static Objects Group(long id, int privacy) => new()
    {
        id = id,
        otype = GraphObjectType.Group,
        data = new JsonObject { ["privacy"] = privacy }.ToJsonString()
    };

    private static Objects Post(long id, short type, int privacy) => new()
    {
        id = id,
        otype = type,
        data = PostJson(type, privacy)
    };

    private static string PostJson(short type, int privacy)
    {
        var data = new JsonObject
        {
            ["create"] = DateTimeOffset.UtcNow.ToString("O")
        };
        if (type != GraphObjectType.GroupPost)
        {
            data["privacy"] = privacy;
        }

        return data.ToJsonString();
    }

    private static Associations Authored(long authorId, long postId) =>
        Edge(authorId, GraphAssociationType.Authored, postId);

    private static Associations AuthoredBy(long postId, long authorId) =>
        Edge(postId, GraphAssociationType.AuthoredBy, authorId);

    private static Associations Edge(long id1, short type, long id2) => new()
    {
        id1 = id1,
        atype = type,
        id2 = id2,
        time = id2
    };
}
