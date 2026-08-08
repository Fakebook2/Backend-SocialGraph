namespace SocialGraph.Api.SubGraphQL;

using System.Globalization;
using HotChocolate;
using SocialGraph.Api.Contracts;
using SocialGraph.Api.Infrastructure;
using SocialGraph.Api.Infrastructure.Outbox;
using SocialGraph.Api.Service;

public class Mutation
{
    private const int MaxRecommendationImpressions = 50;
    private const int MaxImpressionIdempotencyKeyLength = 128;
    private const int MaxImpressionDwellMilliseconds = 900_000;

    public Task<CreateUserPayload> CreateUserAsync(
        CreateUserInput input,
        [Service] IUserGraphService userGraphService,
        CancellationToken cancellationToken)
    {
        return userGraphService.CreateUserAsync(input, cancellationToken);
    }

    public Task<UserProfileResult?> UpdateUserAsync(
        UpdateUserInput input,
        [Service] IUserGraphService userGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(input.Id);
        return userGraphService.UpdateUserAsync(input, cancellationToken);
    }

    public Task<bool> DeleteUserAsync(
        long userId,
        [Service] IUserGraphService userGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(userId);
        return userGraphService.DeleteUserAsync(userId, cancellationToken);
    }

    public Task<UserProfileResult?> ChangeUserAvatarAsync(
        long userId,
        string avatarUrl,
        string? originalUrl,
        int? privacy,
        long? sourceContentId,
        long? sourceMediaId,
        [Service] IUserGraphService userGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(userId);
        return userGraphService.ChangeUserAvatarAsync(
            userId,
            avatarUrl,
            originalUrl,
            privacy ?? 0,
            sourceContentId,
            sourceMediaId,
            cancellationToken);
    }

    public Task<UserProfileResult?> ChangeUserBackgroundAsync(
        long userId,
        string backgroundUrl,
        string? originalUrl,
        int? privacy,
        [Service] IUserGraphService userGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(userId);
        return userGraphService.ChangeUserBackgroundAsync(userId, backgroundUrl, originalUrl, privacy ?? 0, cancellationToken);
    }

    public Task<UserProfileResult?> RemoveUserAvatarAsync(
        long userId,
        [Service] IUserGraphService userGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(userId);
        return userGraphService.ChangeUserAvatarAsync(userId, string.Empty, null, cancellationToken);
    }

    public Task<UserProfileResult?> RemoveUserBackgroundAsync(
        long userId,
        [Service] IUserGraphService userGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(userId);
        return userGraphService.ChangeUserBackgroundAsync(userId, string.Empty, null, cancellationToken);
    }

    public Task<bool> SendFriendRequestAsync(
        long requesterId,
        long receiverId,
        [Service] IUserGraphService userGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(requesterId);
        return userGraphService.SendFriendRequestAsync(requesterId, receiverId, cancellationToken);
    }

    public Task<bool> CancelFriendRequestAsync(
        long requesterId,
        long receiverId,
        [Service] IUserGraphService userGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(requesterId);
        return userGraphService.CancelFriendRequestAsync(requesterId, receiverId, cancellationToken);
    }

    public Task<bool> AcceptFriendRequestAsync(
        long requesterId,
        long receiverId,
        [Service] IUserGraphService userGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(receiverId);
        return userGraphService.AcceptFriendRequestAsync(requesterId, receiverId, cancellationToken);
    }

    public Task<bool> RejectFriendRequestAsync(
        long requesterId,
        long receiverId,
        [Service] IUserGraphService userGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(receiverId);
        return userGraphService.RejectFriendRequestAsync(requesterId, receiverId, cancellationToken);
    }

    public Task<bool> UnfriendAsync(
        long userId,
        long friendId,
        [Service] IUserGraphService userGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(userId);
        return userGraphService.UnfriendAsync(userId, friendId, cancellationToken);
    }

    public Task<bool> FollowUserAsync(long followerId, long targetUserId, [Service] IUserGraphService userGraphService, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(followerId);
        return userGraphService.FollowUserAsync(followerId, targetUserId, cancellationToken);
    }

    public Task<bool> UnfollowUserAsync(long followerId, long targetUserId, [Service] IUserGraphService userGraphService, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(followerId);
        return userGraphService.UnfollowUserAsync(followerId, targetUserId, cancellationToken);
    }

    public Task<bool> BlockUserAsync(long blockerId, long blockedUserId, [Service] IUserGraphService userGraphService, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(blockerId);
        return userGraphService.BlockUserAsync(blockerId, blockedUserId, cancellationToken);
    }

    public Task<bool> UnblockUserAsync(long blockerId, long blockedUserId, [Service] IUserGraphService userGraphService, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(blockerId);
        return userGraphService.UnblockUserAsync(blockerId, blockedUserId, cancellationToken);
    }

    public Task<GroupResult> CreateGroupAsync(CreateGroupInput input, [Service] IGroupGraphService groupGraphService, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(input.CreatorId);
        return groupGraphService.CreateGroupAsync(input, cancellationToken);
    }

    public async Task<GroupResult?> UpdateGroupAsync(UpdateGroupInput input, [Service] IGroupGraphService groupGraphService, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        var actorId = trustedCaller.RequireUserId();
        await RequireGroupAdminAsync(actorId, input.Id, groupGraphService, cancellationToken);
        return await groupGraphService.UpdateGroupAsync(actorId, input, cancellationToken);
    }

    public async Task<bool> DeleteGroupAsync(long groupId, [Service] IGroupGraphService groupGraphService, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        var actorId = trustedCaller.RequireUserId();
        await RequireGroupAdminAsync(actorId, groupId, groupGraphService, cancellationToken);
        return await groupGraphService.DeleteGroupAsync(actorId, groupId, cancellationToken);
    }

    public async Task<GroupResult?> ChangeGroupAvatarAsync(long groupId, string avatarUrl, string? originalUrl, [Service] IGroupGraphService groupGraphService, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        var actorId = trustedCaller.RequireUserId();
        await RequireGroupAdminAsync(actorId, groupId, groupGraphService, cancellationToken);
        return await groupGraphService.ChangeGroupAvatarAsync(actorId, groupId, avatarUrl, originalUrl, cancellationToken);
    }

    public async Task<GroupResult?> ChangeGroupBackgroundAsync(
        long groupId,
        string backgroundUrl,
        string? originalUrl,
        [Service] IGroupGraphService groupGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        var actorId = trustedCaller.RequireUserId();
        await RequireGroupAdminAsync(actorId, groupId, groupGraphService, cancellationToken);
        return await groupGraphService.ChangeGroupBackgroundAsync(actorId, groupId, backgroundUrl, originalUrl, cancellationToken);
    }

    public async Task<GroupResult?> RemoveGroupAvatarAsync(
        long groupId,
        [Service] IGroupGraphService groupGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        var actorId = trustedCaller.RequireUserId();
        await RequireGroupAdminAsync(actorId, groupId, groupGraphService, cancellationToken);
        return await groupGraphService.ChangeGroupAvatarAsync(actorId, groupId, string.Empty, null, cancellationToken);
    }

    public async Task<GroupResult?> RemoveGroupBackgroundAsync(
        long groupId,
        [Service] IGroupGraphService groupGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        var actorId = trustedCaller.RequireUserId();
        await RequireGroupAdminAsync(actorId, groupId, groupGraphService, cancellationToken);
        return await groupGraphService.ChangeGroupBackgroundAsync(actorId, groupId, string.Empty, null, cancellationToken);
    }

    public Task<bool> RecordGroupVisitAsync(
        long userId,
        long groupId,
        [Service] IGroupGraphService groupGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(userId);
        return groupGraphService.RecordGroupVisitAsync(userId, groupId, cancellationToken);
    }

    public Task<bool> RequestJoinGroupAsync(long userId, long groupId, [Service] IGroupGraphService groupGraphService, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(userId);
        return groupGraphService.RequestJoinAsync(userId, groupId, cancellationToken);
    }

    public Task<bool> CancelJoinGroupRequestAsync(long userId, long groupId, [Service] IGroupGraphService groupGraphService, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(userId);
        return groupGraphService.CancelJoinRequestAsync(userId, groupId, cancellationToken);
    }

    public Task<bool> LeaveGroupAsync(long userId, long groupId, [Service] IGroupGraphService groupGraphService, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(userId);
        return groupGraphService.LeaveGroupAsync(userId, groupId, cancellationToken);
    }

    public Task<bool> ApproveGroupJoinRequestAsync(long groupId, long userId, [Service] IGroupGraphService groupGraphService, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        return groupGraphService.ApproveJoinRequestAsync(trustedCaller.RequireUserId(), groupId, userId, cancellationToken);
    }

    public Task<bool> RejectGroupJoinRequestAsync(long groupId, long userId, [Service] IGroupGraphService groupGraphService, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        return groupGraphService.RejectJoinRequestAsync(trustedCaller.RequireUserId(), groupId, userId, cancellationToken);
    }

    public async Task<bool> InviteGroupUserAsync(
        long groupId,
        long userId,
        [Service] IGroupGraphService groupGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        var inviterId = trustedCaller.RequireUserId();
        await RequireGroupParticipantAsync(inviterId, groupId, groupGraphService, cancellationToken);
        return await groupGraphService.InviteUserAsync(inviterId, groupId, userId, cancellationToken);
    }

    public async Task<bool> AddGroupMemberAsync(long groupId, long userId, [Service] IGroupGraphService groupGraphService, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        await RequireGroupAdminAsync(trustedCaller.RequireUserId(), groupId, groupGraphService, cancellationToken);
        return await groupGraphService.AddMemberAsync(groupId, userId, cancellationToken);
    }

    public async Task<bool> RemoveGroupMemberAsync(long groupId, long userId, [Service] IGroupGraphService groupGraphService, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        var adminId = trustedCaller.RequireUserId();
        await RequireGroupAdminAsync(adminId, groupId, groupGraphService, cancellationToken);
        return await groupGraphService.RemoveMemberAsync(adminId, groupId, userId, cancellationToken);
    }

    public async Task<bool> AddGroupAdminAsync(long groupId, long userId, [Service] IGroupGraphService groupGraphService, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        await RequireGroupAdminAsync(trustedCaller.RequireUserId(), groupId, groupGraphService, cancellationToken);
        return await groupGraphService.AddAdminAsync(groupId, userId, cancellationToken);
    }

    public async Task<bool> RemoveGroupAdminAsync(long groupId, long userId, [Service] IGroupGraphService groupGraphService, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        await RequireGroupAdminAsync(trustedCaller.RequireUserId(), groupId, groupGraphService, cancellationToken);
        return await groupGraphService.RemoveAdminAsync(groupId, userId, cancellationToken);
    }

    public Task<ContentResult> CreateFeedPostAsync(
        CreateFeedPostInput input,
        [Service] IContentGraphService contentGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        var actorId = trustedCaller.RequireUserId();
        return contentGraphService.CreateFeedPostAsync(input with { AuthorId = actorId }, cancellationToken);
    }

    public async Task<ContentResult> CreateGroupPostAsync(CreateGroupPostInput input, [Service] IContentGraphService contentGraphService, [Service] IGroupGraphService groupGraphService, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        var actorId = trustedCaller.RequireUserId();
        if (!await groupGraphService.IsParticipantAsync(actorId, input.GroupId, cancellationToken))
        {
            throw Forbidden("Only group members and administrators can publish group posts.");
        }

        return await contentGraphService.CreateGroupPostAsync(input with { AuthorId = actorId }, cancellationToken);
    }

    public async Task<ContentResult?> UpdatePostAsync(UpdatePostInput input, [Service] IContentGraphService contentGraphService, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        await RequireContentAuthorAsync(trustedCaller.RequireUserId(), input.Id, contentGraphService, cancellationToken);
        return await contentGraphService.UpdatePostAsync(input, cancellationToken);
    }

    public async Task<ContentResult?> UpdateCommentAsync(UpdateCommentInput input, [Service] IContentGraphService contentGraphService, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        await RequireContentAuthorAsync(trustedCaller.RequireUserId(), input.Id, contentGraphService, cancellationToken);
        return await contentGraphService.UpdateCommentAsync(input, cancellationToken);
    }

    public async Task<bool> DeleteContentAsync(long contentId, [Service] IContentGraphService contentGraphService, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        if (!await contentGraphService.CanDeleteContentAsync(
                trustedCaller.RequireUserId(),
                contentId,
                cancellationToken))
        {
            throw Forbidden("Only the content author or an administrator of its group can delete this content.");
        }
        return await contentGraphService.DeleteContentAsync(contentId, cancellationToken);
    }

    public async Task<ContentResult> CreateCommentAsync(CreateCommentInput input, [Service] IContentGraphService contentGraphService, [Service] ISocialReadModelService readModels, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        var actorId = trustedCaller.RequireUserId();
        if (!await readModels.CanCommentTargetAsync(actorId, input.TargetId, cancellationToken))
        {
            throw Forbidden("The target is unavailable or not visible to the current user.");
        }

        return await contentGraphService.CreateCommentAsync(input with { AuthorId = actorId }, cancellationToken);
    }

    public Task<NormalStoryResult> CreateNormalStoryAsync(
        CreateNormalStoryInput input,
        [Service] IContentGraphService contentGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        var actorId = trustedCaller.RequireUserId();
        return contentGraphService.CreateNormalStoryAsync(input with { AuthorId = actorId }, cancellationToken);
    }

    public async Task<IHomeStoryResult> CreateShareStoryAsync(
        CreateShareStoryInput input,
        [Service] IContentGraphService contentGraphService,
        [Service] ISocialReadModelService readModels,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        var actorId = trustedCaller.RequireUserId();
        var sourceId = await contentGraphService.ResolveCanonicalShareSourceIdAsync(input.SharedSourceId, cancellationToken);
        if (!await readModels.CanShareStoryTargetAsync(actorId, sourceId, cancellationToken))
        {
            throw Forbidden("The source is unavailable or not visible to the current user.");
        }

        return await contentGraphService.CreateShareStoryAsync(input with { AuthorId = actorId, SharedSourceId = sourceId }, cancellationToken);
    }

    public Task<DeleteStoryPayload> DeleteStoryAsync(
        DeleteStoryInput input,
        [Service] IContentGraphService contentGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        var actorId = trustedCaller.RequireUserId();
        return contentGraphService.DeleteStoryAsync(input with { AuthorId = actorId }, cancellationToken);
    }

    public Task<ContentResult> CreateReelAsync(CreateReelInput input, [Service] IContentGraphService contentGraphService, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        var actorId = trustedCaller.RequireUserId();
        return contentGraphService.CreateReelAsync(input with { AuthorId = actorId }, cancellationToken);
    }

    public async Task<ContentResult> SharePostAsync(SharePostInput input, [Service] IContentGraphService contentGraphService, [Service] ISocialReadModelService readModels, [Service] IGroupGraphService groupGraphService, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        var actorId = trustedCaller.RequireUserId();
        var sourceId = await contentGraphService.ResolveCanonicalShareSourceIdAsync(input.SourceId, cancellationToken);
        if (!await readModels.CanShareTargetAsync(actorId, sourceId, cancellationToken))
        {
            throw Forbidden("The source is unavailable or not visible to the current user.");
        }

        if (input.DestinationGroupId is > 0 &&
            !await groupGraphService.IsParticipantAsync(actorId, input.DestinationGroupId.Value, cancellationToken))
        {
            throw Forbidden("Only group members and administrators can publish group posts.");
        }

        return await contentGraphService.SharePostAsync(input with { AuthorId = actorId, SourceId = sourceId }, cancellationToken);
    }

    public async Task<bool> LikeAsync(long userId, long targetId, [Service] IContentGraphService contentGraphService, [Service] ISocialReadModelService readModels, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(userId);
        if (!await readModels.CanViewTargetAsync(userId, targetId, cancellationToken))
        {
            throw Forbidden("The target is unavailable or not visible to the current user.");
        }

        return await contentGraphService.LikeAsync(userId, targetId, cancellationToken);
    }

    public Task<bool> UnlikeAsync(long userId, long targetId, [Service] IContentGraphService contentGraphService, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(userId);
        return contentGraphService.UnlikeAsync(userId, targetId, cancellationToken);
    }

    public async Task<bool> SaveAsync(long userId, long targetId, [Service] IContentGraphService contentGraphService, [Service] ISocialReadModelService readModels, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(userId);
        if (!await readModels.CanSaveTargetAsync(userId, targetId, cancellationToken))
        {
            throw Forbidden("The target is unavailable or not visible to the current user.");
        }

        return await contentGraphService.SaveAsync(userId, targetId, cancellationToken);
    }

    public Task<bool> UnsaveAsync(long userId, long targetId, [Service] IContentGraphService contentGraphService, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(userId);
        return contentGraphService.UnsaveAsync(userId, targetId, cancellationToken);
    }

    public async Task<bool> WatchAsync(long userId, long targetId, [Service] IContentGraphService contentGraphService, [Service] ISocialReadModelService readModels, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(userId);
        if (!await readModels.CanWatchTargetAsync(userId, targetId, cancellationToken))
        {
            throw Forbidden("The target is unavailable or not visible to the current user.");
        }

        return await contentGraphService.WatchAsync(userId, targetId, cancellationToken);
    }

    public async Task<OperationResult> RecordRecommendationImpressionsAsync(
        RecommendationImpressionInput input,
        [Service] IContentGraphService contentGraphService,
        [Service] IExternalServiceClient externalServiceClient,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        var viewerId = trustedCaller.RequireUserId();
        if (input?.Items is null || input.Items.Count is < 1 or > MaxRecommendationImpressions)
        {
            throw BadUserInput($"An impression batch must contain between 1 and {MaxRecommendationImpressions} items.");
        }

        var normalized = new List<RecommendationImpressionEventItem>(input.Items.Count);
        var targetIds = new HashSet<long>();
        var idempotencyKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in input.Items)
        {
            if (item is null ||
                item.TargetId is null ||
                item.TargetId.Length is < 1 or > 19 ||
                item.TargetId.Any(character => character is < '0' or > '9') ||
                !long.TryParse(item.TargetId, NumberStyles.None, CultureInfo.InvariantCulture, out var targetId) ||
                targetId <= 0 ||
                !targetIds.Add(targetId))
            {
                throw BadUserInput("An impression batch contains an invalid or duplicate target.");
            }

            var clientKey = NormalizeImpressionIdempotencyKey(item.IdempotencyKey);
            if (!idempotencyKeys.Add(clientKey) ||
                item.DwellMs is < 0 or > MaxImpressionDwellMilliseconds ||
                item.CompletionPct is { } completion && (!double.IsFinite(completion) || completion is < 0 or > 100))
            {
                throw BadUserInput("An impression batch contains invalid metrics or duplicate idempotency keys.");
            }

            normalized.Add(new RecommendationImpressionEventItem(
                targetId,
                clientKey,
                item.DwellMs,
                item.CompletionPct));
        }

        // Use the same batched viewer-aware hydrator as Home. It rechecks current privacy,
        // group membership and both block directions. Unavailable IDs are silently omitted so
        // this mutation cannot become an existence/privacy oracle.
        var visiblePosts = await contentGraphService.GetPostDetailsAsync(
            viewerId,
            normalized.Select(item => item.TargetId).ToArray(),
            cancellationToken);
        var visibleIds = visiblePosts.Select(HomePostId).ToHashSet();
        var accepted = normalized.Where(item => visibleIds.Contains(item.TargetId)).ToArray();
        if (accepted.Length > 0)
        {
            await externalServiceClient.RecordRecommendationImpressionsAsync(
                viewerId,
                accepted,
                cancellationToken);
        }

        return new OperationResult(true);
    }

    public async Task<bool> TagAsync(long postId, long userId, [Service] IContentGraphService contentGraphService, [Service] ISocialReadModelService readModels, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        var viewerId = trustedCaller.RequireUserId();
        await RequireContentAuthorAsync(viewerId, postId, contentGraphService, cancellationToken);
        var relationship = await readModels.GetUserRelationshipStateAsync(viewerId, userId, cancellationToken);
        if (relationship is null || relationship.IsBlocked || relationship.IsBlockedBy)
        {
            throw Forbidden("The tagged user is unavailable.");
        }

        return await contentGraphService.TagAsync(postId, userId, cancellationToken);
    }

    public async Task<bool> MentionAsync(long sourceId, long userId, [Service] IContentGraphService contentGraphService, [Service] ISocialReadModelService readModels, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        var viewerId = trustedCaller.RequireUserId();
        await RequireContentAuthorAsync(viewerId, sourceId, contentGraphService, cancellationToken);
        var relationship = await readModels.GetUserRelationshipStateAsync(viewerId, userId, cancellationToken);
        if (relationship is null || relationship.IsBlocked || relationship.IsBlockedBy)
        {
            throw Forbidden("The mentioned user is unavailable.");
        }

        return await contentGraphService.MentionAsync(sourceId, userId, cancellationToken);
    }

    private static async Task RequireGroupAdminAsync(
        long viewerId,
        long groupId,
        IGroupGraphService groupGraphService,
        CancellationToken cancellationToken)
    {
        if (!await groupGraphService.IsAdminAsync(viewerId, groupId, cancellationToken))
        {
            throw Forbidden("Group administrator permission is required.");
        }
    }

    private static async Task RequireGroupParticipantAsync(
        long viewerId,
        long groupId,
        IGroupGraphService groupGraphService,
        CancellationToken cancellationToken)
    {
        if (!await groupGraphService.IsParticipantAsync(viewerId, groupId, cancellationToken))
        {
            throw Forbidden("Current group membership is required.");
        }
    }

    private static async Task RequireContentAuthorAsync(
        long viewerId,
        long contentId,
        IContentGraphService contentGraphService,
        CancellationToken cancellationToken)
    {
        if (!await contentGraphService.IsAuthorAsync(viewerId, contentId, cancellationToken))
        {
            throw Forbidden("Only the content author can perform this operation.");
        }
    }

    private static GraphQLException Forbidden(string message)
    {
        return new GraphQLException(
            ErrorBuilder.New()
                .SetCode("FORBIDDEN")
                .SetMessage(message)
                .Build());
    }

    private static long HomePostId(IHomePostResult post) => post switch
    {
        FeedPostDetailResult feedPost => feedPost.Id,
        GroupPostDetailResult groupPost => groupPost.Id,
        ReelDetailResult reel => reel.Id,
        _ => 0
    };

    private static string NormalizeImpressionIdempotencyKey(string? value)
    {
        var normalized = InputSecurity.RequiredText(
            value,
            "idempotencyKey",
            MaxImpressionIdempotencyKeyLength,
            multiline: false,
            collapseWhitespace: false,
            maxCombiningMarks: 0);
        if (normalized.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.' or ':')))
        {
            throw BadUserInput("Impression idempotency keys contain unsupported characters.");
        }

        return normalized;
    }

    private static GraphQLException BadUserInput(string message)
    {
        return new GraphQLException(
            ErrorBuilder.New()
                .SetCode("BAD_USER_INPUT")
                .SetMessage(message)
                .Build());
    }
}
