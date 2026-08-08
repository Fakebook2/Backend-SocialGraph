namespace SocialGraph.Api.Tests;

using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
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
                new RecommendationImpressionEventItem(456, "viewer-scoped-key", 1_000, 50)
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

        // A different browser key in the same server-owned window cannot create a second
        // stored impression for the same viewer/target.
        await publisher.RecordRecommendationImpressionsAsync(
            123,
            [new RecommendationImpressionEventItem(456, "another-browser-key", 2_000, 75)]);
        Assert.Single(await dbContext.IntegrationOutboxTb.ToListAsync());
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
