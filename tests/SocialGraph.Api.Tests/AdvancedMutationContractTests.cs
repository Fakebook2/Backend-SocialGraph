namespace SocialGraph.Api.Tests;

using HotChocolate;
using HotChocolate.Execution;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SocialGraph.Api.Contracts;
using SocialGraph.Api.Infrastructure;
using SocialGraph.Api.Infrastructure.Outbox;
using SocialGraph.Api.Service;
using SocialGraph.Api.SubGraphQL;

public sealed class AdvancedMutationContractTests
{
    [Fact]
    public async Task RecommendationImpressions_GraphQlIdInputPreservesSnowflakeBeyondJavaScriptSafeInteger()
    {
        const long viewerId = 100;
        const long snowflakeId = 9_007_199_254_740_993;
        var content = new Mock<IContentGraphService>(MockBehavior.Strict);
        content.Setup(item => item.GetPostDetailsAsync(
                viewerId,
                It.Is<IReadOnlyList<long>>(ids => ids.SequenceEqual(new[] { snowflakeId })),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IHomePostResult[]
            {
                new FeedPostDetailResult(
                    snowflakeId,
                    GraphObjectType.FeedPost,
                    "visible",
                    0,
                    "2026-08-09T00:00:00Z",
                    new PostAuthorResult(viewerId, "Viewer", "", false, false),
                    [])
            });
        var external = new Mock<IExternalServiceClient>(MockBehavior.Strict);
        external.Setup(item => item.RecordRecommendationImpressionsAsync(
                viewerId,
                It.Is<IReadOnlyList<RecommendationImpressionEventItem>>(items =>
                    items.Count == 1 && items[0].TargetId == snowflakeId &&
                    items[0].ContentKind == "POST" && items[0].QualityTier == "FAST_SKIP" &&
                    items[0].IsOwnContent == true),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var trusted = new Mock<ITrustedCallerAccessor>(MockBehavior.Strict);
        trusted.Setup(item => item.RequireUserId()).Returns(viewerId);
        var services = new ServiceCollection();
        services.AddSingleton(content.Object);
        services.AddSingleton(external.Object);
        services.AddSingleton(trusted.Object);
        services
            .AddGraphQLServer()
            .AddQueryType<Query>()
            .AddMutationType<Mutation>()
            .AddType<RecommendationItemResult>()
            .AddTypeExtension<RecommendationItemResolvers>()
            .AddType<FeedPostDetailResult>()
            .AddType<ReelDetailResult>()
            .AddType<GroupPostDetailResult>()
            .AddType<NormalStoryResult>()
            .AddType<FeedPostShareStoryResult>()
            .AddType<ReelShareStoryResult>()
            .AddType<FeedPostSharedSourceResult>()
            .AddType<ReelSharedSourceResult>();
        await using var provider = services.BuildServiceProvider();
        var executor = await provider.GetRequiredService<IRequestExecutorProvider>().GetExecutorAsync();
        var request = OperationRequestBuilder.New()
            .SetDocument(
                """
                mutation Record($input: RecommendationImpressionInput!) {
                  recordRecommendationImpressions(input: $input) { success }
                }
                """)
            .SetVariableValues(new Dictionary<string, object?>
            {
                ["input"] = new Dictionary<string, object?>
                {
                    ["items"] = new object[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["targetId"] = snowflakeId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            ["idempotencyKey"] = "browser-snowflake"
                        }
                    }
                }
            })
            .Build();

        var result = await executor.ExecuteAsync(request);

        Assert.Empty(result.ExpectOperationResult().Errors);
        Assert.Contains("targetId: ID!", executor.Schema.ToString());
        Assert.DoesNotContain("contentKind", executor.Schema.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("qualityTier", executor.Schema.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("isOwnContent", executor.Schema.ToString(), StringComparison.OrdinalIgnoreCase);
        content.VerifyAll();
        external.VerifyAll();
        trusted.VerifyAll();
    }

    [Fact]
    public async Task RecommendationImpressions_UsesVisibleBatchAndTrustedActor()
    {
        const long viewerId = 100;
        const long visibleId = 200;
        const long hiddenId = 201;
        var content = new Mock<IContentGraphService>(MockBehavior.Strict);
        content.Setup(item => item.GetPostDetailsAsync(
                viewerId,
                It.Is<IReadOnlyList<long>>(ids => ids.SequenceEqual(new[] { visibleId, hiddenId })),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IHomePostResult[]
            {
                new FeedPostDetailResult(
                    visibleId,
                    GraphObjectType.FeedPost,
                    "visible",
                    0,
                    "2026-08-09T00:00:00Z",
                    new PostAuthorResult(viewerId + 1, "Author", "", false, false),
                    [])
            });
        var external = new Mock<IExternalServiceClient>(MockBehavior.Strict);
        external.Setup(item => item.RecordRecommendationImpressionsAsync(
                viewerId,
                It.Is<IReadOnlyList<RecommendationImpressionEventItem>>(items =>
                    items.Count == 1 && items[0].TargetId == visibleId &&
                    items[0].IdempotencyKey == "browser-key" && items[0].ContentKind == "POST" &&
                    items[0].QualityTier == "SHORT" && items[0].IsOwnContent == false),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var trusted = new Mock<ITrustedCallerAccessor>(MockBehavior.Strict);
        trusted.Setup(item => item.RequireUserId()).Returns(viewerId);

        var result = await new Mutation().RecordRecommendationImpressionsAsync(
            new RecommendationImpressionInput(
                [
                    new RecommendationImpressionItemInput(visibleId.ToString(), "browser-key", 1_000, 50),
                    new RecommendationImpressionItemInput(hiddenId.ToString(), "hidden-key", 1_000, 50)
                ]),
            content.Object,
            external.Object,
            trusted.Object,
            CancellationToken.None);

        Assert.True(result.Success);
        content.VerifyAll();
        external.VerifyAll();
        trusted.VerifyAll();
    }

    [Fact]
    public async Task RecommendationImpressions_DerivesReelKindAfterVisibilityCheck()
    {
        const long viewerId = 100;
        const long reelId = 202;
        var content = new Mock<IContentGraphService>(MockBehavior.Strict);
        content.Setup(item => item.GetPostDetailsAsync(
                viewerId,
                It.Is<IReadOnlyList<long>>(ids => ids.SequenceEqual(new[] { reelId })),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IHomePostResult[]
            {
                new ReelDetailResult(
                    reelId,
                    GraphObjectType.Reel,
                    "visible reel",
                    0,
                    "2026-08-09T00:00:00Z",
                    9d / 16d,
                    0.5d,
                    0.5d,
                    new PostAuthorResult(viewerId, "Viewer", "", false, false),
                    [])
            });
        var external = new Mock<IExternalServiceClient>(MockBehavior.Strict);
        external.Setup(item => item.RecordRecommendationImpressionsAsync(
                viewerId,
                It.Is<IReadOnlyList<RecommendationImpressionEventItem>>(items =>
                    items.Count == 1 && items[0].TargetId == reelId &&
                    items[0].ContentKind == "REEL" && items[0].QualityTier == "LOW" &&
                    items[0].IsOwnContent == true),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var trusted = new Mock<ITrustedCallerAccessor>(MockBehavior.Strict);
        trusted.Setup(item => item.RequireUserId()).Returns(viewerId);

        var result = await new Mutation().RecordRecommendationImpressionsAsync(
            new RecommendationImpressionInput(
                // Ten minutes of real active playback on a very long Reel is not
                // an idle signal merely because completion is still below 5%.
                [new RecommendationImpressionItemInput(reelId.ToString(), "reel-browser-key", 600_000, 4)]),
            content.Object,
            external.Object,
            trusted.Object,
            CancellationToken.None);

        Assert.True(result.Success);
        content.VerifyAll();
        external.VerifyAll();
        trusted.VerifyAll();
    }

    [Fact]
    public async Task RecommendationImpressions_DerivesVideoPostKindAndStrongerHybridTier()
    {
        const long viewerId = 100;
        const long postId = 203;
        var content = new Mock<IContentGraphService>(MockBehavior.Strict);
        content.Setup(item => item.GetPostDetailsAsync(
                viewerId,
                It.Is<IReadOnlyList<long>>(ids => ids.SequenceEqual(new[] { postId })),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IHomePostResult[]
            {
                new FeedPostDetailResult(
                    postId,
                    GraphObjectType.FeedPost,
                    "video post",
                    0,
                    "2026-08-09T00:00:00Z",
                    new PostAuthorResult(viewerId + 1, "Author", "", false, false),
                    [],
                    new SharedPostSourceResult(
                        900,
                        true,
                        GraphObjectType.Reel,
                        "shared video",
                        new UserSummaryResult(viewerId + 2, "Source author", "", false),
                        [new MediaResult(901, 1, "/media/video.mp4")]))
            });
        var external = new Mock<IExternalServiceClient>(MockBehavior.Strict);
        external.Setup(item => item.RecordRecommendationImpressionsAsync(
                viewerId,
                It.Is<IReadOnlyList<RecommendationImpressionEventItem>>(items =>
                    items.Count == 1 && items[0].TargetId == postId &&
                    items[0].ContentKind == "VIDEO_POST" && items[0].QualityTier == "MID" &&
                    items[0].IsOwnContent == false),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var trusted = new Mock<ITrustedCallerAccessor>(MockBehavior.Strict);
        trusted.Setup(item => item.RequireUserId()).Returns(viewerId);

        var result = await new Mutation().RecordRecommendationImpressionsAsync(
            new RecommendationImpressionInput(
                [new RecommendationImpressionItemInput(postId.ToString(), "video-post-key", 10_000, 50)]),
            content.Object,
            external.Object,
            trusted.Object,
            CancellationToken.None);

        Assert.True(result.Success);
        content.VerifyAll();
        external.VerifyAll();
        trusted.VerifyAll();
    }

    [Fact]
    public async Task RecommendationImpressions_RejectsDuplicateTargetsBeforeVisibilityLookup()
    {
        var content = new Mock<IContentGraphService>(MockBehavior.Strict);
        var external = new Mock<IExternalServiceClient>(MockBehavior.Strict);
        var trusted = new Mock<ITrustedCallerAccessor>(MockBehavior.Strict);
        trusted.Setup(item => item.RequireUserId()).Returns(100);

        var exception = await Assert.ThrowsAsync<GraphQLException>(() => new Mutation().RecordRecommendationImpressionsAsync(
            new RecommendationImpressionInput(
                [
                    new RecommendationImpressionItemInput("200", "a"),
                    new RecommendationImpressionItemInput("200", "b")
                ]),
            content.Object,
            external.Object,
            trusted.Object,
            CancellationToken.None));

        Assert.Equal("BAD_USER_INPUT", exception.Errors.Single().Code);
        content.Verify(item => item.GetPostDetailsAsync(
            It.IsAny<long>(),
            It.IsAny<IReadOnlyList<long>>(),
            It.IsAny<CancellationToken>()), Times.Never);
        external.Verify(item => item.RecordRecommendationImpressionsAsync(
            It.IsAny<long>(),
            It.IsAny<IReadOnlyList<RecommendationImpressionEventItem>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RecommendationImpressions_RejectsUnboundedTargetIdBeforeParsingOrLookup()
    {
        var content = new Mock<IContentGraphService>(MockBehavior.Strict);
        var external = new Mock<IExternalServiceClient>(MockBehavior.Strict);
        var trusted = new Mock<ITrustedCallerAccessor>(MockBehavior.Strict);
        trusted.Setup(item => item.RequireUserId()).Returns(100);

        var exception = await Assert.ThrowsAsync<GraphQLException>(() =>
            new Mutation().RecordRecommendationImpressionsAsync(
                new RecommendationImpressionInput(
                    [new RecommendationImpressionItemInput(new string('9', 20), "bounded-key")]),
                content.Object,
                external.Object,
                trusted.Object,
                CancellationToken.None));

        Assert.Equal("BAD_USER_INPUT", exception.Errors.Single().Code);
        content.VerifyNoOtherCalls();
        external.VerifyNoOtherCalls();
        trusted.VerifyAll();
    }

    [Fact]
    public async Task SharePostToGroup_UsesTrustedActorAndRequiresDestinationMembership()
    {
        const long actorId = 100;
        const long spoofedAuthorId = 101;
        const long sourceId = 200;
        const long groupId = 300;
        var content = new Mock<IContentGraphService>(MockBehavior.Strict);
        content.Setup(item => item.ResolveCanonicalShareSourceIdAsync(sourceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(sourceId);
        var reads = new Mock<ISocialReadModelService>(MockBehavior.Strict);
        reads.Setup(item => item.CanShareTargetAsync(actorId, sourceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var groups = new Mock<IGroupGraphService>(MockBehavior.Strict);
        groups.Setup(item => item.IsParticipantAsync(actorId, groupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var trusted = new Mock<ITrustedCallerAccessor>(MockBehavior.Strict);
        trusted.Setup(item => item.RequireUserId()).Returns(actorId);

        var exception = await Assert.ThrowsAsync<GraphQLException>(() => new Mutation().SharePostAsync(
            new SharePostInput(spoofedAuthorId, sourceId, "share", 0, groupId),
            content.Object,
            reads.Object,
            groups.Object,
            trusted.Object,
            CancellationToken.None));

        Assert.Equal("FORBIDDEN", exception.Errors.Single().Code);
        content.Verify(item => item.SharePostAsync(It.IsAny<SharePostInput>(), It.IsAny<CancellationToken>()), Times.Never);
        content.VerifyAll();
        reads.VerifyAll();
        groups.VerifyAll();
        trusted.VerifyAll();
    }

    [Fact]
    public async Task SharePostToGroup_ForwardsOnlyTrustedActorAndCanonicalSource()
    {
        const long actorId = 100;
        const long sourceId = 200;
        const long canonicalSourceId = 201;
        const long groupId = 300;
        var expected = new ContentResult(400, GraphObjectType.GroupPost, "share", 0, "now", actorId, Array.Empty<MediaResult>());
        var content = new Mock<IContentGraphService>(MockBehavior.Strict);
        content.Setup(item => item.ResolveCanonicalShareSourceIdAsync(sourceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(canonicalSourceId);
        content.Setup(item => item.SharePostAsync(
                It.Is<SharePostInput>(input => input.AuthorId == actorId && input.SourceId == canonicalSourceId && input.DestinationGroupId == groupId),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        var reads = new Mock<ISocialReadModelService>(MockBehavior.Strict);
        reads.Setup(item => item.CanShareTargetAsync(actorId, canonicalSourceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var groups = new Mock<IGroupGraphService>(MockBehavior.Strict);
        groups.Setup(item => item.IsParticipantAsync(actorId, groupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var trusted = new Mock<ITrustedCallerAccessor>(MockBehavior.Strict);
        trusted.Setup(item => item.RequireUserId()).Returns(actorId);

        var result = await new Mutation().SharePostAsync(
            new SharePostInput(999, sourceId, "share", 0, groupId),
            content.Object,
            reads.Object,
            groups.Object,
            trusted.Object,
            CancellationToken.None);

        Assert.Same(expected, result);
        content.VerifyAll();
        reads.VerifyAll();
        groups.VerifyAll();
        trusted.VerifyAll();
    }

    [Fact]
    public async Task DeleteContent_AllowsGroupAdministratorThroughTheCanonicalPolicy()
    {
        const long adminId = 100;
        const long postId = 200;
        var content = new Mock<IContentGraphService>(MockBehavior.Strict);
        content.Setup(item => item.CanDeleteContentAsync(adminId, postId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        content.Setup(item => item.DeleteContentAsync(postId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var trusted = new Mock<ITrustedCallerAccessor>(MockBehavior.Strict);
        trusted.Setup(item => item.RequireUserId()).Returns(adminId);

        var deleted = await new Mutation().DeleteContentAsync(
            postId,
            content.Object,
            trusted.Object,
            CancellationToken.None);

        Assert.True(deleted);
        content.VerifyAll();
        trusted.VerifyAll();
    }

    [Fact]
    public async Task DeleteContent_RejectsCallerOutsideTheCanonicalPolicy()
    {
        const long viewerId = 100;
        const long postId = 200;
        var content = new Mock<IContentGraphService>(MockBehavior.Strict);
        content.Setup(item => item.CanDeleteContentAsync(viewerId, postId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var trusted = new Mock<ITrustedCallerAccessor>(MockBehavior.Strict);
        trusted.Setup(item => item.RequireUserId()).Returns(viewerId);

        var exception = await Assert.ThrowsAsync<GraphQLException>(() => new Mutation().DeleteContentAsync(
            postId,
            content.Object,
            trusted.Object,
            CancellationToken.None));

        Assert.Equal("FORBIDDEN", exception.Errors.Single().Code);
        content.Verify(item => item.DeleteContentAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateComment_RejectsANonAuthorBeforeTheDomainWrite()
    {
        const long viewerId = 100;
        const long commentId = 200;
        var content = new Mock<IContentGraphService>(MockBehavior.Strict);
        content.Setup(item => item.IsAuthorAsync(viewerId, commentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var trusted = new Mock<ITrustedCallerAccessor>(MockBehavior.Strict);
        trusted.Setup(item => item.RequireUserId()).Returns(viewerId);

        var exception = await Assert.ThrowsAsync<GraphQLException>(() => new Mutation().UpdateCommentAsync(
            new UpdateCommentInput(commentId, "spoofed edit"),
            content.Object,
            trusted.Object,
            CancellationToken.None));

        Assert.Equal("FORBIDDEN", exception.Errors.Single().Code);
        content.Verify(item => item.UpdateCommentAsync(It.IsAny<UpdateCommentInput>(), It.IsAny<CancellationToken>()), Times.Never);
        content.VerifyAll();
        trusted.VerifyAll();
    }

    [Fact]
    public async Task UpdateComment_UsesTheTrustedCallerAndForwardsOnlyAfterOwnershipPasses()
    {
        const long viewerId = 100;
        const long commentId = 200;
        var input = new UpdateCommentInput(commentId, "edited");
        var expected = new ContentResult(commentId, GraphObjectType.Comment, "edited", 0, "now", viewerId, []);
        var content = new Mock<IContentGraphService>(MockBehavior.Strict);
        content.Setup(item => item.IsAuthorAsync(viewerId, commentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        content.Setup(item => item.UpdateCommentAsync(input, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        var trusted = new Mock<ITrustedCallerAccessor>(MockBehavior.Strict);
        trusted.Setup(item => item.RequireUserId()).Returns(viewerId);

        var result = await new Mutation().UpdateCommentAsync(input, content.Object, trusted.Object, CancellationToken.None);

        Assert.Same(expected, result);
        content.VerifyAll();
        trusted.VerifyAll();
    }

    [Fact]
    public async Task ChangeUserAvatar_UsesTrustedOwnerAndForwardsExactSourcePair()
    {
        const long userId = 100;
        const long contentId = 9_000_000_000_000_121;
        const long mediaId = 9_000_000_000_000_122;
        var users = new Mock<IUserGraphService>(MockBehavior.Strict);
        users.Setup(item => item.ChangeUserAvatarAsync(
                userId,
                "/media/cropped.jpg",
                null,
                0,
                contentId,
                mediaId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserProfileResult?)null);
        var trusted = new Mock<ITrustedCallerAccessor>(MockBehavior.Strict);
        trusted.Setup(item => item.RequireUserId(userId)).Returns(userId);

        await new Mutation().ChangeUserAvatarAsync(
            userId,
            "/media/cropped.jpg",
            null,
            0,
            contentId,
            mediaId,
            users.Object,
            trusted.Object,
            CancellationToken.None);

        users.VerifyAll();
        trusted.VerifyAll();
    }

    [Fact]
    public async Task RemoveUserAvatar_UsesTrustedOwnerAndEmptyUrlSemantics()
    {
        const long userId = 100;
        var users = new Mock<IUserGraphService>(MockBehavior.Strict);
        users.Setup(item => item.ChangeUserAvatarAsync(userId, string.Empty, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserProfileResult?)null);
        var trusted = new Mock<ITrustedCallerAccessor>(MockBehavior.Strict);
        trusted.Setup(item => item.RequireUserId(userId)).Returns(userId);

        await new Mutation().RemoveUserAvatarAsync(userId, users.Object, trusted.Object, CancellationToken.None);

        users.VerifyAll();
        trusted.VerifyAll();
    }

    [Fact]
    public async Task InviteGroupUser_RequiresTrustedCurrentParticipant()
    {
        const long inviterId = 100;
        const long groupId = 200;
        const long userId = 300;
        var groups = new Mock<IGroupGraphService>(MockBehavior.Strict);
        groups.Setup(item => item.IsParticipantAsync(inviterId, groupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        groups.Setup(item => item.InviteUserAsync(inviterId, groupId, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var trusted = new Mock<ITrustedCallerAccessor>(MockBehavior.Strict);
        trusted.Setup(item => item.RequireUserId()).Returns(inviterId);

        var result = await new Mutation().InviteGroupUserAsync(
            groupId,
            userId,
            groups.Object,
            trusted.Object,
            CancellationToken.None);

        Assert.True(result);
        groups.VerifyAll();
        trusted.VerifyAll();
    }

    [Fact]
    public async Task InviteGroupUser_RejectsAuthenticatedNonParticipantBeforeDispatch()
    {
        const long outsiderId = 101;
        const long groupId = 200;
        const long userId = 300;
        var groups = new Mock<IGroupGraphService>(MockBehavior.Strict);
        groups.Setup(item => item.IsParticipantAsync(outsiderId, groupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var trusted = new Mock<ITrustedCallerAccessor>(MockBehavior.Strict);
        trusted.Setup(item => item.RequireUserId()).Returns(outsiderId);

        await Assert.ThrowsAsync<GraphQLException>(() => new Mutation().InviteGroupUserAsync(
            groupId,
            userId,
            groups.Object,
            trusted.Object,
            CancellationToken.None));

        groups.Verify(item => item.InviteUserAsync(
            It.IsAny<long>(),
            It.IsAny<long>(),
            It.IsAny<long>(),
            It.IsAny<CancellationToken>()), Times.Never);
        groups.VerifyAll();
        trusted.VerifyAll();
    }
}
