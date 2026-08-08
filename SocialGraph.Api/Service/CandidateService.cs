namespace SocialGraph.Api.Service;

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SocialGraph.Api.Contracts;
using SocialGraph.Api.Database;

public sealed class CandidateService : ICandidateService
{
    private readonly MyDbContext _dbContext;

    public CandidateService(MyDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<long>> GetPostCandidateIdsAsync(
        long userId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var candidates = await GetPostCandidateSnapshotAsync(userId, limit, cancellationToken);
        return candidates.Select(item => item.Id).ToArray();
    }

    public async Task<IReadOnlyList<RecommendationCandidateResult>> GetPostCandidatesAsync(
        long userId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        return await GetPostCandidateSnapshotAsync(userId, limit, cancellationToken);
    }

    private async Task<IReadOnlyList<RecommendationCandidateResult>> GetPostCandidateSnapshotAsync(
        long userId,
        int limit,
        CancellationToken cancellationToken)
    {
        var take = Math.Clamp(limit, 1, 500);
        var viewer = await GetViewerCandidateContextAsync(userId, cancellationToken);
        var selfCandidates = new Dictionary<long, RecommendationCandidateResult>();
        var friendCandidates = new Dictionary<long, RecommendationCandidateResult>();
        var followedCandidates = new Dictionary<long, RecommendationCandidateResult>();
        var memberGroupCandidates = new Dictionary<long, RecommendationCandidateResult>();
        var publicFeedCandidates = new Dictionary<long, RecommendationCandidateResult>();
        var publicGroupCandidates = new Dictionary<long, RecommendationCandidateResult>();

        var eligibleAuthors = viewer.Friends
            .Concat(viewer.Followed)
            .Append(userId)
            .Where(authorId => authorId == userId || !viewer.Blocked.Contains(authorId))
            .Distinct()
            .ToArray();
        var perAuthor = Math.Min(
            take,
            Math.Max(3, (take * 3 + eligibleAuthors.Length - 1) / Math.Max(1, eligibleAuthors.Length)));
        var selfLimit = Math.Min(take, Math.Max(5, (take + 9) / 10));
        var authoredRows = await GetNewestFeedLikePerAuthorAsync(
            eligibleAuthors,
            userId,
            perAuthor,
            selfLimit,
            cancellationToken);
        var acceptedPerAuthor = new Dictionary<long, int>();
        foreach (var row in authoredRows)
        {
            var privacy = GraphJson.Int(GraphJson.ParseObject(row.Data), "privacy");
            if (privacy is < 0 or > 3)
            {
                continue;
            }

            if (row.AuthorId == userId)
            {
                if (!TryTakeAuthorQuota(acceptedPerAuthor, row.AuthorId, selfLimit))
                {
                    continue;
                }

                AddRecommendationCandidate(selfCandidates, row, "self");
            }
            else if (viewer.Friends.Contains(row.AuthorId) && privacy <= 2)
            {
                if (!TryTakeAuthorQuota(acceptedPerAuthor, row.AuthorId, perAuthor))
                {
                    continue;
                }

                AddRecommendationCandidate(friendCandidates, row, "friend");
            }
            else if (viewer.Followed.Contains(row.AuthorId) && privacy <= 1)
            {
                if (!TryTakeAuthorQuota(acceptedPerAuthor, row.AuthorId, perAuthor))
                {
                    continue;
                }

                AddRecommendationCandidate(followedCandidates, row, "followed");
            }
        }

        await AddMemberGroupCandidatesAsync(
            selfCandidates,
            memberGroupCandidates,
            userId,
            viewer.Groups,
            viewer.Blocked,
            take,
            cancellationToken);
        await AddRecentPublicFeedCandidatesAsync(
            selfCandidates,
            publicFeedCandidates,
            userId,
            viewer.Blocked,
            take,
            cancellationToken);
        await AddPublicGroupCandidatesAsync(
            selfCandidates,
            publicGroupCandidates,
            userId,
            viewer.Blocked,
            take,
            cancellationToken);

        var candidateById = selfCandidates.Values
            .Concat(friendCandidates.Values)
            .Concat(followedCandidates.Values)
            .Concat(memberGroupCandidates.Values)
            .Concat(publicFeedCandidates.Values)
            .Concat(publicGroupCandidates.Values)
            .GroupBy(item => item.Id)
            .ToDictionary(group => group.Key, group => group.First());
        var orderedIds = MergeCandidateSources(
            take,
            [
                OrderCandidateIdsByAuthor(selfCandidates.Values, item => item.Id, item => item.AuthorId),
                OrderCandidateIdsByAuthor(friendCandidates.Values, item => item.Id, item => item.AuthorId),
                OrderCandidateIdsByAuthor(followedCandidates.Values, item => item.Id, item => item.AuthorId),
                OrderCandidateIdsByAuthor(memberGroupCandidates.Values, item => item.Id, item => item.AuthorId),
                OrderCandidateIdsByAuthor(publicFeedCandidates.Values, item => item.Id, item => item.AuthorId),
                OrderCandidateIdsByAuthor(publicGroupCandidates.Values, item => item.Id, item => item.AuthorId)
            ],
            // self 10%, friend 20%, followed 20%, joined group 20%, public feed 20%,
            // public group 10%. Empty sources donate their slots deterministically.
            [0, 1, 2, 3, 4, 5, 1, 2, 3, 4],
            preserveSourceOrder: true);

        return orderedIds.Select(id => candidateById[id]).ToArray();
    }

    private sealed record ViewerRelationRow(long Id2, short Atype, long Time);

    private sealed record ViewerCandidateContext(
        HashSet<long> Friends,
        HashSet<long> Followed,
        HashSet<long> Groups,
        HashSet<long> Blocked);

    private async Task<ViewerCandidateContext> GetViewerCandidateContextAsync(
        long userId,
        CancellationToken cancellationToken)
    {
        var relevantTypes = new short[]
        {
            GraphAssociationType.Friend,
            GraphAssociationType.Followed,
            GraphAssociationType.Member,
            GraphAssociationType.Admin,
            GraphAssociationType.Blocked,
            GraphAssociationType.BlockedBy
        };
        IReadOnlyList<ViewerRelationRow> rows;
        if (IsInMemory())
        {
            rows = await _dbContext.AssociationsTb
                .AsNoTracking()
                .Where(item => item.id1 == userId && relevantTypes.Contains(item.atype))
                .Select(item => new ViewerRelationRow(item.id2, item.atype, item.time))
                .ToListAsync(cancellationToken);
        }
        else
        {
            // One bounded relationship snapshot replaces the former six sequential lookups.
            // Block edges remain complete; every other source is capped independently at 200.
            rows = await _dbContext.Database.SqlQuery<ViewerRelationRow>($"""
                SELECT "Id2", "Atype", "Time"
                FROM (
                    SELECT a.id2 AS "Id2",
                           a.atype AS "Atype",
                           a."time" AS "Time",
                           ROW_NUMBER() OVER (
                               PARTITION BY a.atype
                               ORDER BY a."time" DESC, a.id2 DESC) AS rn
                    FROM social_graph.associations a
                    WHERE a.id1 = {userId}
                      AND a.atype IN (
                          {GraphAssociationType.Friend},
                          {GraphAssociationType.Followed},
                          {GraphAssociationType.Member},
                          {GraphAssociationType.Admin},
                          {GraphAssociationType.Blocked},
                          {GraphAssociationType.BlockedBy})
                ) ranked
                WHERE "Atype" IN ({GraphAssociationType.Blocked}, {GraphAssociationType.BlockedBy})
                   OR rn <= 200
                """).ToListAsync(cancellationToken);
        }

        HashSet<long> Latest(short type) => rows
            .Where(item => item.Atype == type)
            .OrderByDescending(item => item.Time)
            .ThenByDescending(item => item.Id2)
            .Take(200)
            .Select(item => item.Id2)
            .ToHashSet();

        var memberGroups = Latest(GraphAssociationType.Member);
        memberGroups.UnionWith(Latest(GraphAssociationType.Admin));
        var blocked = rows
            .Where(item => item.Atype is GraphAssociationType.Blocked or GraphAssociationType.BlockedBy)
            .Select(item => item.Id2)
            .ToHashSet();
        return new ViewerCandidateContext(
            Latest(GraphAssociationType.Friend),
            Latest(GraphAssociationType.Followed),
            memberGroups,
            blocked);
    }

    private sealed record FeedLikeCandidateRow(
        long Id,
        string Data,
        long AuthorId,
        short ContentType);

    private sealed record GroupCandidateRow(
        long PostId,
        long AuthorId,
        long GroupId,
        string PostData);

    private async Task<IReadOnlyList<FeedLikeCandidateRow>> GetNewestFeedLikePerAuthorAsync(
        long[] authorIds,
        long viewerId,
        int perAuthor,
        int selfLimit,
        CancellationToken cancellationToken)
    {
        if (authorIds.Length == 0)
        {
            return Array.Empty<FeedLikeCandidateRow>();
        }

        // Privacy is evaluated against the current viewer after this query. Overfetching a
        // bounded window prevents a run of newer, non-visible posts from hiding older eligible
        // posts while keeping the total rows bounded independently of an author's history.
        var perAuthorWindow = GetPolicyOverfetchLimit(perAuthor);
        var selfWindow = GetPolicyOverfetchLimit(selfLimit);
        if (IsInMemory())
        {
            var results = new List<FeedLikeCandidateRow>();
            foreach (var authorId in authorIds)
            {
                var authored = await (
                    from association in _dbContext.AssociationsTb.AsNoTracking()
                    join obj in _dbContext.ObjectsTb.AsNoTracking() on association.id2 equals obj.id
                    where association.id1 == authorId &&
                        association.atype == GraphAssociationType.Authored &&
                        (obj.otype == GraphObjectType.FeedPost || obj.otype == GraphObjectType.Reel)
                    orderby association.time descending, obj.id descending
                    select new FeedLikeCandidateRow(obj.id, obj.data, authorId, obj.otype))
                    .Take(authorId == viewerId ? selfWindow : perAuthorWindow)
                    .ToListAsync(cancellationToken);
                results.AddRange(authored);
            }

            return results;
        }

        return await _dbContext.Database.SqlQuery<FeedLikeCandidateRow>($"""
            SELECT candidate."Id",
                   candidate."Data",
                   requested."AuthorId",
                   candidate."ContentType"
            FROM unnest({authorIds}) AS requested("AuthorId")
            CROSS JOIN LATERAL (
                SELECT o.id AS "Id",
                       o.data AS "Data",
                       o.otype AS "ContentType"
                FROM social_graph.associations a
                JOIN social_graph.objects o ON o.id = a.id2
                WHERE a.atype = {GraphAssociationType.Authored}
                  AND a.id1 = requested."AuthorId"
                  AND o.otype IN ({GraphObjectType.FeedPost}, {GraphObjectType.Reel})
                ORDER BY a."time" DESC, o.id DESC
                LIMIT CASE
                    WHEN requested."AuthorId" = {viewerId} THEN {selfWindow}
                    ELSE {perAuthorWindow}
                END
            ) candidate
            ORDER BY candidate."Id" DESC
            """).ToListAsync(cancellationToken);
    }

    private static void AddRecommendationCandidate(
        IDictionary<long, RecommendationCandidateResult> destination,
        FeedLikeCandidateRow row,
        string source)
    {
        destination.TryAdd(
            row.Id,
            new RecommendationCandidateResult(
                row.Id,
                row.AuthorId,
                source,
                NormalizeCreatedAt(row.Data),
                row.ContentType));
    }

    private static int GetPolicyOverfetchLimit(int quota) =>
        Math.Min(500, Math.Max(quota * 4, quota + 12));

    private static bool TryTakeAuthorQuota(
        IDictionary<long, int> acceptedPerAuthor,
        long authorId,
        int quota)
    {
        acceptedPerAuthor.TryGetValue(authorId, out var accepted);
        if (accepted >= quota)
        {
            return false;
        }

        acceptedPerAuthor[authorId] = accepted + 1;
        return true;
    }

    private async Task AddMemberGroupCandidatesAsync(
        IDictionary<long, RecommendationCandidateResult> selfCandidates,
        IDictionary<long, RecommendationCandidateResult> groupCandidates,
        long viewerId,
        IReadOnlyCollection<long> groupIds,
        ISet<long> blocked,
        int limit,
        CancellationToken cancellationToken)
    {
        if (groupIds.Count == 0)
        {
            return;
        }

        var ids = groupIds.ToArray();
        var rows = await (
            from published in _dbContext.AssociationsTb.AsNoTracking()
            join post in _dbContext.ObjectsTb.AsNoTracking() on published.id2 equals post.id
            join authoredBy in _dbContext.AssociationsTb.AsNoTracking() on post.id equals authoredBy.id1
            where ids.Contains(published.id1) &&
                published.atype == GraphAssociationType.Published &&
                post.otype == GraphObjectType.GroupPost &&
                authoredBy.atype == GraphAssociationType.AuthoredBy
            orderby post.id descending
            select new GroupCandidateRow(post.id, authoredBy.id2, published.id1, post.data))
            .Take(Math.Min(10_000, limit * 12))
            .ToListAsync(cancellationToken);

        foreach (var row in PrioritizeRowsByAuthor(
                     rows.Where(row => !blocked.Contains(row.AuthorId)),
                     row => row.PostId,
                     row => row.AuthorId,
                     limit * 3,
                     PublicDiscoveryPerAuthorLimit(limit)))
        {
            if (row.AuthorId <= 0)
            {
                continue;
            }

            var candidate = new RecommendationCandidateResult(
                row.PostId,
                row.AuthorId,
                row.AuthorId == viewerId ? "self" : "group_member",
                NormalizeCreatedAt(row.PostData),
                GraphObjectType.GroupPost,
                row.GroupId);
            (row.AuthorId == viewerId ? selfCandidates : groupCandidates).TryAdd(row.PostId, candidate);
        }
    }

    private async Task AddRecentPublicFeedCandidatesAsync(
        IDictionary<long, RecommendationCandidateResult> selfCandidates,
        IDictionary<long, RecommendationCandidateResult> publicCandidates,
        long viewerId,
        ISet<long> blocked,
        int limit,
        CancellationToken cancellationToken)
    {
        var rows = await GetRecentPublicFeedLikeCandidatesAsync(limit, blocked, cancellationToken);

        foreach (var row in rows)
        {
            if (row.AuthorId <= 0 || viewerId != row.AuthorId && blocked.Contains(row.AuthorId))
            {
                continue;
            }

            var candidate = new RecommendationCandidateResult(
                row.Id,
                row.AuthorId,
                row.AuthorId == viewerId ? "self" : "recent_public",
                NormalizeCreatedAt(row.Data),
                row.ContentType);
            (row.AuthorId == viewerId ? selfCandidates : publicCandidates).TryAdd(row.Id, candidate);
        }
    }

    private async Task<IReadOnlyList<FeedLikeCandidateRow>> GetRecentPublicFeedLikeCandidatesAsync(
        int limit,
        ISet<long> blocked,
        CancellationToken cancellationToken)
    {
        var perAuthor = PublicDiscoveryPerAuthorLimit(limit);
        var blockedIds = blocked.ToArray();
        var scanLimit = Math.Min(10_000, Math.Max(limit * 20, limit * 3));
        if (IsInMemory())
        {
            var rows = await (
                from post in _dbContext.ObjectsTb.AsNoTracking()
                join authoredBy in _dbContext.AssociationsTb.AsNoTracking() on post.id equals authoredBy.id1
                where (post.otype == GraphObjectType.FeedPost || post.otype == GraphObjectType.Reel) &&
                    authoredBy.atype == GraphAssociationType.AuthoredBy
                orderby post.id descending
                select new FeedLikeCandidateRow(post.id, post.data, authoredBy.id2, post.otype))
                .Take(scanLimit)
                .ToListAsync(cancellationToken);
            var visible = rows
                .Where(row => !blocked.Contains(row.AuthorId) &&
                    GraphJson.Int(GraphJson.ParseObject(row.Data), "privacy") == 0)
                .GroupBy(row => row.AuthorId)
                .Select(group => group.OrderByDescending(row => row.Id).ToArray())
                .ToArray();
            return visible
                .SelectMany(group => group.Take(perAuthor))
                .OrderByDescending(row => row.Id)
                .Concat(visible.SelectMany(group => group.Skip(perAuthor)).OrderByDescending(row => row.Id))
                .Take(limit * 3)
                .ToArray();
        }

        const string publicPrivacyJson = "{\"privacy\":0}";
        return await _dbContext.Database.SqlQuery<FeedLikeCandidateRow>($"""
            WITH recent_raw AS (
                SELECT o.id AS "Id",
                       o.data AS "Data",
                       a.id2 AS "AuthorId",
                       o.otype AS "ContentType"
                FROM social_graph.objects o
                JOIN social_graph.associations a ON a.id1 = o.id
                WHERE a.atype = {GraphAssociationType.AuthoredBy}
                  AND o.otype IN ({GraphObjectType.FeedPost}, {GraphObjectType.Reel})
                ORDER BY o.id DESC
                LIMIT {scanLimit}
            ), filtered AS (
                SELECT *
                FROM recent_raw
                WHERE "Data"::jsonb @> {publicPrivacyJson}::jsonb
                  AND NOT ("AuthorId" = ANY({blockedIds}))
            )
            SELECT "Id", "Data", "AuthorId", "ContentType"
            FROM (
                SELECT filtered.*,
                       ROW_NUMBER() OVER (PARTITION BY "AuthorId" ORDER BY "Id" DESC) AS rn
                FROM filtered
            ) ranked
            ORDER BY CASE WHEN rn <= {perAuthor} THEN 0 ELSE 1 END, "Id" DESC
            LIMIT {limit * 3}
            """).ToListAsync(cancellationToken);
    }

    private async Task AddPublicGroupCandidatesAsync(
        IDictionary<long, RecommendationCandidateResult> selfCandidates,
        IDictionary<long, RecommendationCandidateResult> publicCandidates,
        long viewerId,
        ISet<long> blocked,
        int limit,
        CancellationToken cancellationToken)
    {
        var rows = await GetRecentPublicGroupCandidateRowsAsync(limit, blocked, cancellationToken);

        foreach (var row in rows)
        {
            if (row.AuthorId <= 0)
            {
                continue;
            }

            var candidate = new RecommendationCandidateResult(
                row.PostId,
                row.AuthorId,
                row.AuthorId == viewerId ? "self" : "public_group",
                NormalizeCreatedAt(row.PostData),
                GraphObjectType.GroupPost,
                row.GroupId);
            (row.AuthorId == viewerId ? selfCandidates : publicCandidates).TryAdd(row.PostId, candidate);
        }
    }

    private async Task<IReadOnlyList<GroupCandidateRow>> GetRecentPublicGroupCandidateRowsAsync(
        int limit,
        ISet<long> blocked,
        CancellationToken cancellationToken)
    {
        var perAuthor = PublicDiscoveryPerAuthorLimit(limit);
        var blockedIds = blocked.ToArray();
        var scanLimit = Math.Min(10_000, Math.Max(limit * 20, limit * 3));
        if (IsInMemory())
        {
            var rows = await (
                from published in _dbContext.AssociationsTb.AsNoTracking()
                join post in _dbContext.ObjectsTb.AsNoTracking() on published.id2 equals post.id
                join groupObject in _dbContext.ObjectsTb.AsNoTracking() on published.id1 equals groupObject.id
                join authoredBy in _dbContext.AssociationsTb.AsNoTracking() on post.id equals authoredBy.id1
                where published.atype == GraphAssociationType.Published &&
                    post.otype == GraphObjectType.GroupPost &&
                    groupObject.otype == GraphObjectType.Group &&
                    authoredBy.atype == GraphAssociationType.AuthoredBy
                orderby post.id descending
                select new
                {
                    Row = new GroupCandidateRow(post.id, authoredBy.id2, published.id1, post.data),
                    GroupData = groupObject.data
                })
                .Take(scanLimit)
                .ToListAsync(cancellationToken);
            return PrioritizeRowsByAuthor(
                rows.Where(row => !blocked.Contains(row.Row.AuthorId) &&
                        GraphJson.Int(GraphJson.ParseObject(row.GroupData), "privacy") == 0)
                    .Select(row => row.Row),
                row => row.PostId,
                row => row.AuthorId,
                limit * 6,
                perAuthor);
        }

        const string publicPrivacyJson = "{\"privacy\":0}";
        return await _dbContext.Database.SqlQuery<GroupCandidateRow>($"""
            WITH recent_raw AS (
                SELECT post.id AS "PostId",
                       authored.id2 AS "AuthorId",
                       published.id1 AS "GroupId",
                       post.data AS "PostData",
                       group_object.data AS "GroupData"
                FROM social_graph.associations published
                JOIN social_graph.objects post ON post.id = published.id2
                JOIN social_graph.objects group_object ON group_object.id = published.id1
                JOIN social_graph.associations authored ON authored.id1 = post.id
                WHERE published.atype = {GraphAssociationType.Published}
                  AND post.otype = {GraphObjectType.GroupPost}
                  AND group_object.otype = {GraphObjectType.Group}
                  AND authored.atype = {GraphAssociationType.AuthoredBy}
                ORDER BY post.id DESC
                LIMIT {scanLimit}
            ), filtered AS (
                SELECT "PostId", "AuthorId", "GroupId", "PostData"
                FROM recent_raw
                WHERE "GroupData"::jsonb @> {publicPrivacyJson}::jsonb
                  AND NOT ("AuthorId" = ANY({blockedIds}))
            )
            SELECT "PostId", "AuthorId", "GroupId", "PostData"
            FROM (
                SELECT filtered.*,
                       ROW_NUMBER() OVER (PARTITION BY "AuthorId" ORDER BY "PostId" DESC) AS rn
                FROM filtered
            ) ranked
            ORDER BY CASE WHEN rn <= {perAuthor} THEN 0 ELSE 1 END, "PostId" DESC
            LIMIT {limit * 6}
            """).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CandidateItemResult>> GetReelCandidatesAsync(
        long userId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        return await GetReelCandidatesAsync(userId, limit, "FOR_YOU", cancellationToken);
    }

    public async Task<IReadOnlyList<CandidateItemResult>> GetReelCandidatesAsync(
        long userId,
        int limit,
        string mode,
        CancellationToken cancellationToken = default)
    {
        var normalizedMode = mode.Trim().ToUpperInvariant();
        if (normalizedMode is not ("FOR_YOU" or "FOLLOWING"))
        {
            throw new ArgumentException("mode must be FOR_YOU or FOLLOWING.", nameof(mode));
        }

        var take = Math.Clamp(limit, 1, 500);
        var viewer = await GetViewerCandidateContextAsync(userId, cancellationToken);
        var blocked = viewer.Blocked;
        var selfCandidates = new Dictionary<long, CandidateItemResult>();
        var friendCandidates = new Dictionary<long, CandidateItemResult>();
        var followedCandidates = new Dictionary<long, CandidateItemResult>();
        var publicCandidates = new Dictionary<long, CandidateItemResult>();

        if (normalizedMode == "FOR_YOU")
        {
            await AddAuthorCandidatesAsync(selfCandidates, [userId], GraphObjectType.Reel, "self", blocked, take, maxVisiblePrivacy: 3, cancellationToken: cancellationToken);
        }
        await AddAuthorCandidatesAsync(friendCandidates, viewer.Friends.ToArray(), GraphObjectType.Reel, "friend", blocked, take, maxVisiblePrivacy: 2, cancellationToken: cancellationToken);
        await AddAuthorCandidatesAsync(followedCandidates, viewer.Followed.ToArray(), GraphObjectType.Reel, "followed", blocked, take, maxVisiblePrivacy: 1, cancellationToken: cancellationToken);
        if (normalizedMode == "FOR_YOU")
        {
            await AddRecentCandidatesAsync(publicCandidates, GraphObjectType.Reel, "recent_public", blocked, take, cancellationToken);
        }

        var candidateById = selfCandidates.Values
            .Concat(friendCandidates.Values)
            .Concat(followedCandidates.Values)
            .Concat(publicCandidates.Values)
            .GroupBy(item => item.Id)
            .ToDictionary(group => group.Key, group => group.First());
        var orderedIds = normalizedMode == "FOLLOWING"
            ? MergeCandidateSources(
                take,
                [
                    OrderCandidateIdsByAuthor(friendCandidates.Values, item => item.Id, item => item.AuthorId),
                    OrderCandidateIdsByAuthor(followedCandidates.Values, item => item.Id, item => item.AuthorId)
                ],
                [0, 1],
                preserveSourceOrder: true)
            : MergeCandidateSources(
                take,
                // self 10%, friends 30%, follows 30%, discovery 30%. Duplicates in
                // recent_public are skipped after their relationship source wins.
                [
                    OrderCandidateIdsByAuthor(selfCandidates.Values, item => item.Id, item => item.AuthorId),
                    OrderCandidateIdsByAuthor(friendCandidates.Values, item => item.Id, item => item.AuthorId),
                    OrderCandidateIdsByAuthor(followedCandidates.Values, item => item.Id, item => item.AuthorId),
                    OrderCandidateIdsByAuthor(publicCandidates.Values, item => item.Id, item => item.AuthorId)
                ],
                [0, 1, 2, 3, 1, 2, 3, 1, 2, 3],
                preserveSourceOrder: true);

        return orderedIds.Select(id => candidateById[id]).ToArray();
    }

    private async Task AddAuthorCandidatesAsync(
        Dictionary<long, CandidateItemResult> candidates,
        IReadOnlyList<long> authorIds,
        short objectType,
        string source,
        ISet<long> blocked,
        int limit,
        int maxVisiblePrivacy,
        CancellationToken cancellationToken)
    {
        var eligibleAuthors = authorIds.Where(id => !blocked.Contains(id)).Distinct().ToArray();
        if (eligibleAuthors.Length == 0)
        {
            return;
        }

        // Oversample roughly three candidate pages across the whole relationship set, not
        // `limit / 2` for every author (which could return 50,000 rows for 200 friends). A lone
        // relationship can still contribute a complete pool, while broad graphs stay bounded.
        var perAuthor = Math.Min(
            limit,
            Math.Max(3, (limit * 3 + eligibleAuthors.Length - 1) / eligibleAuthors.Length));
        var rows = await GetNewestPerAuthorAsync(
            eligibleAuthors,
            objectType,
            GetPolicyOverfetchLimit(perAuthor),
            cancellationToken);

        var acceptedPerAuthor = new Dictionary<long, int>();
        foreach (var row in rows)
        {
            var privacy = GraphJson.Int(GraphJson.ParseObject(row.Data), "privacy");
            if (privacy < 0 || privacy > maxVisiblePrivacy ||
                !TryTakeAuthorQuota(acceptedPerAuthor, row.AuthorId, perAuthor))
            {
                continue;
            }

            AddCandidate(candidates, row.Id, row.AuthorId, row.Data, source, blocked);
        }
    }

    private sealed record AuthoredCandidateRow(long Id, string Data, long AuthorId);

    /// <summary>
    /// The newest <paramref name="perAuthor"/> objects of the given type for each author.
    /// </summary>
    /// <remarks>
    /// This ran one query per author, and the caller passes up to a few hundred of them for a
    /// single feed request. One indexed lateral query answers the whole set while bounding each
    /// author's history scan, so one prolific author cannot crowd everyone else out — which a
    /// single globally-ordered query with one overall limit would allow.
    /// </remarks>
    private async Task<IReadOnlyList<AuthoredCandidateRow>> GetNewestPerAuthorAsync(
        long[] authorIds,
        short objectType,
        int perAuthor,
        CancellationToken cancellationToken)
    {
        if (IsInMemory())
        {
            // The in-memory provider cannot translate a window function; tests run against
            // fixtures small enough that the original per-author queries are fine.
            var results = new List<AuthoredCandidateRow>();
            foreach (var authorId in authorIds)
            {
                var authored = await (
                    from association in _dbContext.AssociationsTb.AsNoTracking()
                    join obj in _dbContext.ObjectsTb.AsNoTracking() on association.id2 equals obj.id
                    where association.id1 == authorId &&
                        association.atype == GraphAssociationType.Authored &&
                        obj.otype == objectType
                    orderby association.time descending
                    select new AuthoredCandidateRow(obj.id, obj.data, authorId))
                    .Take(perAuthor)
                    .ToListAsync(cancellationToken);
                results.AddRange(authored);
            }

            return results;
        }

        return await _dbContext.Database
            .SqlQuery<AuthoredCandidateRow>($"""
                SELECT candidate."Id", candidate."Data", requested."AuthorId"
                FROM unnest({authorIds}) AS requested("AuthorId")
                CROSS JOIN LATERAL (
                    SELECT o.id AS "Id",
                           o.data AS "Data"
                    FROM social_graph.associations a
                    JOIN social_graph.objects o ON o.id = a.id2
                    WHERE a.atype = {GraphAssociationType.Authored}
                      AND a.id1 = requested."AuthorId"
                      AND o.otype = {objectType}
                    ORDER BY a."time" DESC, o.id DESC
                    LIMIT {perAuthor}
                ) candidate
                """)
            .ToListAsync(cancellationToken);
    }

    private bool IsInMemory() => string.Equals(
        _dbContext.Database.ProviderName,
        "Microsoft.EntityFrameworkCore.InMemory",
        StringComparison.Ordinal);

    private async Task AddRecentCandidatesAsync(
        Dictionary<long, CandidateItemResult> candidates,
        short objectType,
        string source,
        ISet<long> blocked,
        int limit,
        CancellationToken cancellationToken)
    {
        var eligible = await GetRecentPublicCandidatesPerAuthorAsync(
            objectType,
            limit,
            blocked,
            cancellationToken);

        foreach (var row in eligible)
        {
            AddCandidate(
                candidates,
                row.Id,
                row.AuthorId,
                row.Data,
                source,
                blocked);
        }
    }

    private async Task<IReadOnlyList<AuthoredCandidateRow>> GetRecentPublicCandidatesPerAuthorAsync(
        short objectType,
        int limit,
        ISet<long> blocked,
        CancellationToken cancellationToken)
    {
        var perAuthor = PublicDiscoveryPerAuthorLimit(limit);
        var blockedIds = blocked.ToArray();
        var scanLimit = Math.Min(10_000, Math.Max(limit * 20, limit * 3));
        if (IsInMemory())
        {
            var rows = await (
                from obj in _dbContext.ObjectsTb.AsNoTracking()
                join authoredBy in _dbContext.AssociationsTb.AsNoTracking() on obj.id equals authoredBy.id1
                where obj.otype == objectType && authoredBy.atype == GraphAssociationType.AuthoredBy
                orderby obj.id descending
                select new AuthoredCandidateRow(obj.id, obj.data, authoredBy.id2))
                .Take(scanLimit)
                .ToListAsync(cancellationToken);
            var visible = rows
                .Where(row => !blocked.Contains(row.AuthorId) &&
                    GraphJson.Int(GraphJson.ParseObject(row.Data), "privacy") == 0)
                .GroupBy(row => row.AuthorId)
                .Select(group => group.OrderByDescending(row => row.Id).ToArray())
                .ToArray();
            return visible
                .SelectMany(group => group.Take(perAuthor))
                .OrderByDescending(row => row.Id)
                .Concat(visible.SelectMany(group => group.Skip(perAuthor)).OrderByDescending(row => row.Id))
                .Take(limit * 3)
                .ToArray();
        }

        const string publicPrivacyJson = "{\"privacy\":0}";
        return await _dbContext.Database.SqlQuery<AuthoredCandidateRow>($"""
            WITH recent_raw AS (
                SELECT o.id AS "Id",
                       o.data AS "Data",
                       a.id2 AS "AuthorId"
                FROM social_graph.objects o
                JOIN social_graph.associations a ON a.id1 = o.id
                WHERE a.atype = {GraphAssociationType.AuthoredBy}
                  AND o.otype = {objectType}
                ORDER BY o.id DESC
                LIMIT {scanLimit}
            ), filtered AS (
                SELECT *
                FROM recent_raw
                WHERE "Data"::jsonb @> {publicPrivacyJson}::jsonb
                  AND NOT ("AuthorId" = ANY({blockedIds}))
            )
            SELECT "Id", "Data", "AuthorId"
            FROM (
                SELECT filtered.*,
                       ROW_NUMBER() OVER (PARTITION BY "AuthorId" ORDER BY "Id" DESC) AS rn
                FROM filtered
            ) ranked
            ORDER BY CASE WHEN rn <= {perAuthor} THEN 0 ELSE 1 END, "Id" DESC
            LIMIT {limit * 3}
            """).ToListAsync(cancellationToken);
    }

    private static int PublicDiscoveryPerAuthorLimit(int limit) =>
        Math.Clamp((limit + 9) / 10, 3, 25);

    private static void AddCandidate(
        Dictionary<long, CandidateItemResult> candidates,
        long objectId,
        long authorId,
        string dataJson,
        string source,
        ISet<long> blocked)
    {
        if (authorId <= 0 || blocked.Contains(authorId) || candidates.ContainsKey(objectId))
        {
            return;
        }

        var data = GraphJson.ParseObject(dataJson);
        candidates[objectId] = new CandidateItemResult(
            objectId,
            authorId,
            source,
            GraphJson.String(data, "create"));
    }

    private static string NormalizeCreatedAt(string dataJson)
    {
        var raw = GraphJson.String(GraphJson.ParseObject(dataJson), "create");
        if (DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var createdAt))
        {
            return createdAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        }

        // Legacy rows without a valid timestamp remain safe to rank; the recommendation
        // service treats the epoch as maximally stale rather than guessing from an object ID.
        return DateTimeOffset.UnixEpoch.ToString("O", CultureInfo.InvariantCulture);
    }

    private static IReadOnlyList<long> MergeCandidateSources(
        int limit,
        IReadOnlyCollection<long>[] sources,
        ReadOnlySpan<int> schedule,
        bool preserveSourceOrder = false)
    {
        if (limit <= 0 || sources.Length == 0)
        {
            return Array.Empty<long>();
        }

        var ordered = sources
            .Select(source => preserveSourceOrder
                ? source.Distinct().ToArray()
                : source.Distinct().OrderByDescending(id => id).ToArray())
            .ToArray();
        var offsets = new int[ordered.Length];
        var seen = new HashSet<long>();
        var result = new List<long>(limit);

        while (result.Count < limit)
        {
            var addedInCycle = false;
            foreach (var sourceIndex in schedule)
            {
                if (sourceIndex >= ordered.Length)
                {
                    continue;
                }

                var source = ordered[sourceIndex];
                while (offsets[sourceIndex] < source.Length)
                {
                    var candidateId = source[offsets[sourceIndex]++];
                    if (!seen.Add(candidateId))
                    {
                        continue;
                    }

                    result.Add(candidateId);
                    addedInCycle = true;
                    break;
                }

                if (result.Count == limit)
                {
                    break;
                }
            }

            if (!addedInCycle)
            {
                break;
            }
        }

        return result;
    }

    private static long[] OrderCandidateIdsByAuthor<T>(
        IEnumerable<T> candidates,
        Func<T, long> idSelector,
        Func<T, long> authorSelector)
    {
        var authorQueues = candidates
            .GroupBy(authorSelector)
            .Select(group => group
                .OrderByDescending(idSelector)
                .Select(idSelector)
                .ToArray())
            .Where(queue => queue.Length > 0)
            .OrderByDescending(queue => queue[0])
            .ToArray();
        if (authorQueues.Length == 0)
        {
            return [];
        }

        var offsets = new int[authorQueues.Length];
        var ordered = new List<long>(authorQueues.Sum(queue => queue.Length));
        while (true)
        {
            var added = false;
            for (var index = 0; index < authorQueues.Length; index++)
            {
                if (offsets[index] >= authorQueues[index].Length)
                {
                    continue;
                }

                ordered.Add(authorQueues[index][offsets[index]++]);
                added = true;
            }

            if (!added)
            {
                return ordered.ToArray();
            }
        }
    }

    private static IReadOnlyList<T> PrioritizeRowsByAuthor<T>(
        IEnumerable<T> rows,
        Func<T, long> idSelector,
        Func<T, long> authorSelector,
        int limit,
        int primaryPerAuthor)
    {
        var authorRows = rows
            .GroupBy(authorSelector)
            .Select(group => group.OrderByDescending(idSelector).ToArray())
            .ToArray();
        return authorRows
            .SelectMany(group => group.Take(primaryPerAuthor))
            .OrderByDescending(idSelector)
            .Concat(authorRows
                .SelectMany(group => group.Skip(primaryPerAuthor))
                .OrderByDescending(idSelector))
            .Take(limit)
            .ToArray();
    }

}
