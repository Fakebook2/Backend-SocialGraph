namespace SocialGraph.Api.Tests;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Moq;
using SocialGraph.Api.Database;
using SocialGraph.Api.Infrastructure.Outbox;
using SocialGraph.Api.Service;

public sealed class IntegrationOutboxPublisherTests
{
    private const string EncryptionKey = "integration-outbox-test-key-at-least-32-bytes";

    [Fact]
    public async Task CreateUser_QueuesOnlyAuthGateAndEncryptsCredentials()
    {
        await using var dbContext = CreateDbContext();
        var store = new PostgresIntegrationOutboxStore(
            dbContext,
            Options.Create(new IntegrationOutboxOptions()));
        var configuration = Configuration();
        var protector = new OutboxPayloadProtector(configuration);
        var context = new DefaultHttpContext();
        context.TraceIdentifier = "trace-id";
        context.Request.Headers["Idempotency-Key"] = "register-request-1";
        var publisher = new IntegrationOutboxPublisher(
            store,
            new HttpContextAccessor { HttpContext = context },
            protector);

        await publisher.CreateUserAsync(123, "a@example.com", "plain-password", "Nguyen A", "2000-01-01", true);
        await publisher.CreateUserAsync(123, "a@example.com", "plain-password", "Nguyen A", "2000-01-01", true);

        var messages = await dbContext.IntegrationOutboxTb.OrderBy(item => item.event_type).ToListAsync();
        var auth = Assert.Single(messages);
        Assert.Equal(IntegrationEventType.UserCreate, auth.event_type);
        Assert.DoesNotContain("plain-password", auth.payload, StringComparison.Ordinal);
        Assert.DoesNotContain("a@example.com", auth.payload, StringComparison.Ordinal);
        var decrypted = JsonSerializer.Deserialize<UserCreateEvent>(
            protector.Unprotect(auth.payload),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(decrypted);
        Assert.Equal("plain-password", decrypted.Password);
    }

    [Fact]
    public async Task ExplicitIdempotencyKey_DeduplicatesClientRetries()
    {
        await using var dbContext = CreateDbContext();
        var store = new PostgresIntegrationOutboxStore(
            dbContext,
            Options.Create(new IntegrationOutboxOptions()));
        var configuration = Configuration();
        var context = new DefaultHttpContext();
        context.Request.Headers["Idempotency-Key"] = "same-client-operation";
        var publisher = new IntegrationOutboxPublisher(
            store,
            new HttpContextAccessor { HttpContext = context },
            new OutboxPayloadProtector(configuration));

        await publisher.NotifyAsync(1, 2, 4, 1, null);
        await publisher.NotifyAsync(1, 2, 4, 1, null);

        Assert.Equal(1, await dbContext.IntegrationOutboxTb.CountAsync());
    }

    [Fact]
    public async Task RecommendationInteraction_QueuesCanonicalFeedbackEvent()
    {
        await using var dbContext = CreateDbContext();
        var store = new PostgresIntegrationOutboxStore(
            dbContext,
            Options.Create(new IntegrationOutboxOptions()));
        var configuration = Configuration();
        var context = new DefaultHttpContext();
        context.Request.Headers["Idempotency-Key"] = "save-operation";
        var publisher = new IntegrationOutboxPublisher(
            store,
            new HttpContextAccessor { HttpContext = context },
            new OutboxPayloadProtector(configuration));

        await publisher.RecordRecommendationInteractionAsync(123, 456, "SAVE");
        await publisher.RecordRecommendationInteractionAsync(123, 456, "SAVE");

        var message = Assert.Single(await dbContext.IntegrationOutboxTb.ToListAsync());
        Assert.Equal(IntegrationEventType.RecommendationInteraction, message.event_type);
        Assert.Equal(123, message.aggregate_id);
        var payload = JsonSerializer.Deserialize<RecommendationInteractionEvent>(
            message.payload,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(payload);
        Assert.Equal(123, payload.UserId);
        Assert.Equal(456, payload.TargetId);
        Assert.Equal("SAVE", payload.Action);
        Assert.InRange(payload.OccurredAt!.Value, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
    }

    [Fact]
    public async Task RecommendationInteraction_UsesTrustedDatabaseClockAndKeepsRetryKeyStable()
    {
        var trustedTime = DateTimeOffset.Parse("2026-08-09T03:04:05Z");
        var store = new Mock<IIntegrationOutboxStore>(MockBehavior.Strict);
        var writes = new List<(string Key, string Payload)>();
        store.SetupSequence(item => item.GetCurrentTimeAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(trustedTime)
            .ReturnsAsync(trustedTime.AddSeconds(1));
        store.Setup(item => item.EnqueueAsync(
                IntegrationEventType.RecommendationInteraction,
                123,
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string eventType, long? aggregateId, string key, string payload, CancellationToken _) =>
            {
                writes.Add((key, payload));
                return new IntegrationOutboxMessage
                {
                    event_type = eventType,
                    aggregate_id = aggregateId,
                    idempotency_key = key,
                    payload = payload
                };
            });
        var context = new DefaultHttpContext();
        context.Request.Headers["Idempotency-Key"] = "trusted-save-operation";
        var publisher = new IntegrationOutboxPublisher(
            store.Object,
            new HttpContextAccessor { HttpContext = context },
            new OutboxPayloadProtector(Configuration()));

        await publisher.RecordRecommendationInteractionAsync(123, 456, "SAVE");
        await publisher.RecordRecommendationInteractionAsync(123, 456, "SAVE");

        Assert.Equal(2, writes.Count);
        Assert.Equal(writes[0].Key, writes[1].Key);
        var legacyKeySource =
            "recommendation.interaction.v1:123:trusted-save-operation:{\"userId\":123,\"targetId\":456,\"action\":\"SAVE\"}";
        var expectedLegacyKey = "socialgraph-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(legacyKeySource))).ToLowerInvariant();
        Assert.Equal(expectedLegacyKey, writes[0].Key);
        var first = JsonSerializer.Deserialize<RecommendationInteractionEvent>(
            writes[0].Payload,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var second = JsonSerializer.Deserialize<RecommendationInteractionEvent>(
            writes[1].Payload,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(trustedTime, first!.OccurredAt);
        Assert.Equal(trustedTime.AddSeconds(1), second!.OccurredAt);
        store.VerifyAll();
    }

    [Fact]
    public async Task RecommendationImpressions_QueuesBoundedScopedBatch()
    {
        await using var dbContext = CreateDbContext();
        var store = new PostgresIntegrationOutboxStore(
            dbContext,
            Options.Create(new IntegrationOutboxOptions()));
        var publisher = new IntegrationOutboxPublisher(
            store,
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            new OutboxPayloadProtector(Configuration()));

        await publisher.RecordRecommendationImpressionsAsync(
            123,
            [
                new RecommendationImpressionEventItem(456, "viewer-scoped-key", 1_000, 50, "VIDEO_POST", "MID", false)
            ]);

        var message = Assert.Single(await dbContext.IntegrationOutboxTb.ToListAsync());
        Assert.Equal(IntegrationEventType.RecommendationImpressions, message.event_type);
        var payload = JsonSerializer.Deserialize<RecommendationImpressionEvent>(
            message.payload,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(payload);
        Assert.Equal(123, payload.UserId);
        Assert.InRange(payload.ObservedAt, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
        var item = Assert.Single(payload.Items);
        Assert.Equal(456, item.TargetId);
        Assert.StartsWith("socialgraph-", item.IdempotencyKey, StringComparison.Ordinal);
        Assert.NotEqual("viewer-scoped-key", item.IdempotencyKey);
        Assert.Equal(1_000, item.DwellMs);
        Assert.Equal(50, item.CompletionPct);
        Assert.Equal("VIDEO_POST", item.ContentKind);
        Assert.Equal("MID", item.QualityTier);
        Assert.False(item.IsOwnContent);

        // A different browser key in the same server-owned window cannot create a second
        // stored impression for the same viewer/target.
        await publisher.RecordRecommendationImpressionsAsync(
            123,
            [new RecommendationImpressionEventItem(456, "another-browser-key", 2_000, 55, "VIDEO_POST", "MID", false)]);
        Assert.Single(await dbContext.IntegrationOutboxTb.ToListAsync());

        // A richer server-derived tier is intentionally a second bounded update.
        await publisher.RecordRecommendationImpressionsAsync(
            123,
            [new RecommendationImpressionEventItem(456, "richer-browser-key", 5_000, 75, "VIDEO_POST", "HIGH", false)]);
        Assert.Equal(2, await dbContext.IntegrationOutboxTb.CountAsync());
    }

    [Fact]
    public async Task RecommendationImpressions_RejectsInventedOrUnboundedQualityTier()
    {
        await using var dbContext = CreateDbContext();
        var publisher = new IntegrationOutboxPublisher(
            new PostgresIntegrationOutboxStore(dbContext, Options.Create(new IntegrationOutboxOptions())),
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            new OutboxPayloadProtector(Configuration()));

        await Assert.ThrowsAsync<ArgumentException>(() => publisher.RecordRecommendationImpressionsAsync(
            123,
            [new RecommendationImpressionEventItem(456, "key", 1_000, 50, "POST", "COMPLETE")]));
        await Assert.ThrowsAsync<ArgumentException>(() => publisher.RecordRecommendationImpressionsAsync(
            123,
            [new RecommendationImpressionEventItem(456, "key", 600_000, 4, "REEL", "IDLE")]));
        Assert.Empty(await dbContext.IntegrationOutboxTb.ToListAsync());
    }

    [Fact]
    public async Task MediaLifecycle_DeduplicatesWithinBatchButKeepsLaterSameSlotReattach()
    {
        await using var dbContext = CreateDbContext();
        var store = new PostgresIntegrationOutboxStore(dbContext, Options.Create(new IntegrationOutboxOptions()));
        var configuration = Configuration();
        var context = new DefaultHttpContext();
        context.Request.Headers["Idempotency-Key"] = "media-operation";
        var publisher = new IntegrationOutboxPublisher(
            store,
            new HttpContextAccessor { HttpContext = context },
            new OutboxPayloadProtector(configuration));

        var attached = new MediaLifecycleReference("/media/files/a.jpg", "socialgraph:media:100");
        var detached = new MediaLifecycleReference("/media/files/b.jpg", "socialgraph:media:200");
        await publisher.FinalizeMediaAsync(new[] { attached, attached }, 42);
        await Task.Delay(1);
        await publisher.FinalizeMediaAsync(new[] { attached }, 42);
        await publisher.DeleteMediaAsync(new[] { detached }, 42);

        var messages = await dbContext.IntegrationOutboxTb.OrderBy(item => item.event_type).ToListAsync();
        Assert.Equal(3, messages.Count);
        Assert.Equal(2, messages.Count(item => item.event_type == IntegrationEventType.MediaFinalize));
        Assert.Contains(messages, item => item.event_type == IntegrationEventType.MediaDelete);
        var finalizePayloads = messages
            .Where(item => item.event_type == IntegrationEventType.MediaFinalize)
            .Select(item => JsonSerializer.Deserialize<MediaLifecycleEvent>(
                item.payload,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)))
            .ToArray();
        Assert.All(finalizePayloads, finalize =>
        {
            Assert.Null(finalize?.Urls);
            Assert.Equal(new[] { attached }, finalize?.References);
            Assert.NotNull(finalize?.OperationAt);
        });
        Assert.NotEqual(finalizePayloads[0]!.OperationAt, finalizePayloads[1]!.OperationAt);
    }

    private static IConfiguration Configuration()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["IntegrationOutbox:PayloadEncryptionKey"] = "",
                ["InternalServices:SocialGraph:SharedSecret"] = EncryptionKey
            })
            .Build();
    }

    private static MyDbContext CreateDbContext()
    {
        return new MyDbContext(new DbContextOptionsBuilder<MyDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);
    }
}
