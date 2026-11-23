using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using HospitalInformationSystem.Application.Audit.Services;
using HospitalInformationSystem.Application.Models.Audit;
using HospitalInformationSystem.Application.Models.Security;
using HospitalInformationSystem.Infrastructure.Audit;
using HospitalInformationSystem.Infrastructure.Contexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class AuditQueryServiceTests(HospitalApiFactory factory) : IClassFixture<HospitalApiFactory>
{
    [Fact]
    public async Task Get_AdministratorRequest_ShouldReturnPagedAuditContract()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/audit?page=1&pageSize=1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await response.Content.ReadFromJsonAsync<PagedResult<AuditEventDto>>();
        page.Should().NotBeNull();
        page!.Page.Should().Be(1);
        page.PageSize.Should().Be(1);
        page.Items.Count.Should().BeLessThanOrEqualTo(1);
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("pageSize=101")]
    [InlineData("eventType=999")]
    [InlineData("from=2040-02-02T00:00:00Z&to=2040-02-01T00:00:00Z")]
    public async Task Get_InvalidQuery_ShouldReturnBadRequest(string query)
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/audit?{query}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task List_SameTimestampAndPageBoundary_ShouldUseStableEventIdOrderAndFilters()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var eventFactory = new AuditEventFactory(TimeProvider.System);
        var actor = new AuditActor(AuditActorKind.System, null, "Audit query test", null, "integration-test");
        var at = new DateTimeOffset(2041, 4, 1, 12, 23, 45, TimeSpan.Zero);
        var ids = new[]
        {
            Guid.Parse("00000000-0000-0000-0000-000000000003"),
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            Guid.Parse("00000000-0000-0000-0000-000000000002")
        };
        foreach (var id in ids)
        {
            var entry = eventFactory.Create(AuditEventType.AccountChanged, "Активность аккаунта изменена.", actor,
                new AuditMetadata { Action = "activity-change", TargetId = Guid.NewGuid(), RevokedRefreshTokenCount = 0 });
            entry.EventId = id;
            entry.EventTimeUtc = at;
            db.AuditEvents.Add(entry);
        }
        var excluded = eventFactory.Create(AuditEventType.PatientCreated, "Пациент создан.", actor);
        excluded.EventTimeUtc = at;
        db.AuditEvents.Add(excluded);
        await db.SaveChangesAsync(CancellationToken.None);
        db.ChangeTracker.Clear();

        var service = new AuditQueryService(new AuditRepository(db));
        var query = new AuditQuery(PageSize: 2, EventType: AuditEventType.AccountChanged,
            From: at.ToOffset(TimeSpan.FromHours(3)), To: at);
        var first = await service.ListAsync(query, CancellationToken.None);
        var second = await service.ListAsync(query with { Page = 2 }, CancellationToken.None);

        first.Items.Select(entry => entry.EventId).Should().Equal(ids.OrderDescending().Take(2));
        first.HasMore.Should().BeTrue();
        second.Items.Should().ContainSingle().Which.EventId.Should().Be(ids.Min());
        second.HasMore.Should().BeFalse();
        first.Items.Should().OnlyContain(entry => entry.EventType == AuditEventType.AccountChanged);
        first.Items[0].Actor.Kind.Should().Be(AuditActorKind.System);
        first.Items[0].Metadata["revokedRefreshTokenCount"].GetInt32().Should().Be(0);
        db.ChangeTracker.Entries().Should().BeEmpty();
    }

    [Fact]
    public async Task Append_RolledBackBusinessTransaction_ShouldNotLeaveAuditEvent()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var writer = new AuditWriter(new AuditRepository(db), new AuditEntryFactory(TimeProvider.System), new SystemAuditActorAccessor());
        Guid eventId;
        await using (var transaction = await db.Database.BeginTransactionAsync(CancellationToken.None))
        {
            writer.Append(AuditEventType.AccountChanged, "Активность аккаунта изменена.",
                new AuditMetadata { Action = "activity-change", TargetId = Guid.NewGuid() });
            eventId = db.ChangeTracker.Entries<Application.Models.Persistence.AuditEventDbo>()
                .Single().Entity.EventId;
            await db.SaveChangesAsync(CancellationToken.None);
            await transaction.RollbackAsync(CancellationToken.None);
        }
        db.ChangeTracker.Clear();

        (await db.AuditEvents.AnyAsync(entry => entry.EventId == eventId, CancellationToken.None)).Should().BeFalse();
    }
}
