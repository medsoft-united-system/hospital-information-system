using FluentAssertions;
using HospitalInformationSystem.Application.Audit.Services;
using HospitalInformationSystem.Infrastructure.Audit;
using HospitalInformationSystem.Infrastructure.Contexts;
using HospitalInformationSystem.Models.Audit;
using HospitalInformationSystem.Models.Common;
using HospitalInformationSystem.Models.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class ServiceAuditPersistenceTests(FhirReadApiFactory factory) : IClassFixture<FhirReadApiFactory>
{
    [Fact]
    public async Task Query_ServiceLogins_ShouldSeparateNewAndLegacyServiceEventsFromEmployeeLogins()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var at = new DateTimeOffset(2042, 3, 5, 5, 6, 7, TimeSpan.Zero);
        var events = new AuditEventFactory(new FixedTimeProvider(at));
        var actor = new AuditActor(AuditActorKind.ServiceAccount, null, "lis.external", "request", null,
            FhirReadApiFactory.ServiceId);
        var legacy = events.Create(AuditEventType.UserLogin, "Сервис получил сессию.", actor);
        var current = events.Create(AuditEventType.ServiceLogin, "Сервис получил сессию.", actor);
        var employee = events.Create(AuditEventType.UserLogin, "Сотрудник получил сессию.",
            new AuditActor(AuditActorKind.User, Guid.NewGuid(), "Врач", "request", null));
        db.AuditEvents.AddRange(legacy, current, employee);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var service = new AuditQueryService(new AuditRepository(db));

        var all = await service.ListAsync(new AuditQuery(From: at, To: at), CancellationToken.None);
        var serviceLogins = await service.ListAsync(new AuditQuery(EventType: AuditEventType.ServiceLogin,
            From: at, To: at), CancellationToken.None);
        var employeeLogins = await service.ListAsync(new AuditQuery(EventType: AuditEventType.UserLogin,
            From: at, To: at), CancellationToken.None);

        all.Items.Where(item => item.Actor.Kind == AuditActorKind.ServiceAccount)
            .Should().HaveCount(2).And.OnlyContain(item => item.EventType == AuditEventType.ServiceLogin);
        serviceLogins.Items.Select(item => item.EventId).Should().BeEquivalentTo([legacy.EventId, current.EventId]);
        serviceLogins.Items.Should().OnlyContain(item => item.EventType == AuditEventType.ServiceLogin);
        employeeLogins.Items.Should().ContainSingle().Which.EventId.Should().Be(employee.EventId);
        (await db.AuditEvents.AsNoTracking().SingleAsync(item => item.EventId == legacy.EventId))
            .EventType.Should().Be(nameof(AuditEventType.UserLogin));
    }

    [Fact]
    public async Task SaveAndQuery_ServiceActor_ShouldPreserveServiceIdentityWithoutHumanUser()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var at = new DateTimeOffset(2042, 3, 4, 5, 6, 7, TimeSpan.Zero);
        var actor = new AuditActor(AuditActorKind.ServiceAccount, null, "lis.external", "service-request", null,
            FhirReadApiFactory.ServiceId);
        var entry = new AuditEventFactory(new FixedTimeProvider(at)).Create(AuditEventType.MedicalRecordChanged,
            "Зарегистрирован результат анализа.", actor,
            new AuditMetadata { Action = "LaboratoryAnalysisResultRecorded", RecordEntryId = Guid.NewGuid() });
        db.AuditEvents.Add(entry);
        await db.SaveChangesAsync(CancellationToken.None);
        db.ChangeTracker.Clear();

        var page = await new AuditQueryService(new AuditRepository(db)).ListAsync(new AuditQuery(From: at, To: at), CancellationToken.None);

        var restored = page.Items.Should().ContainSingle(item => item.EventId == entry.EventId).Which;
        restored.Actor.Kind.Should().Be(AuditActorKind.ServiceAccount);
        restored.Actor.ServiceAccountId.Should().Be(FhirReadApiFactory.ServiceId);
        restored.Actor.UserId.Should().BeNull();
        restored.Actor.DisplayName.Should().Be("lis.external");
        restored.Actor.RequestId.Should().Be("service-request");
        restored.Metadata["correlationId"].GetString().Should().Be("service-request");
    }

    [Theory]
    [InlineData("service", false, false)]
    [InlineData("service", true, true)]
    [InlineData("user", true, true)]
    [InlineData("anonymous", false, true)]
    [InlineData("system", false, true)]
    public async Task Save_InvalidServiceActorShape_ShouldRejectAtDatabaseBoundary(string kind, bool hasUser,
        bool hasService)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var entry = new AuditEventFactory(TimeProvider.System).Create(AuditEventType.MedicalRecordChanged,
            "Изменена ЭМК.", new SystemAuditActorAccessor().GetCurrentActor());
        entry.ActorKind = kind;
        entry.ActorUserId = hasUser ? Guid.NewGuid() : null;
        entry.ActorServiceAccountId = hasService ? FhirReadApiFactory.ServiceId : null;
        db.AuditEvents.Add(entry);

        var save = () => db.SaveChangesAsync(CancellationToken.None);

        await save.Should().ThrowAsync<BusinessConflictException>();
        db.ChangeTracker.Clear();
        (await db.AuditEvents.AnyAsync(item => item.EventId == entry.EventId)).Should().BeFalse();
    }

    [Fact]
    public async Task Save_NonexistentServiceActor_ShouldRejectForeignKeyWithoutAuditEntry()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var entry = new AuditEventFactory(TimeProvider.System).Create(AuditEventType.MedicalRecordChanged,
            "Изменена ЭМК.", new AuditActor(AuditActorKind.ServiceAccount, null, "missing.lis", "request", null,
                Guid.NewGuid()));
        db.AuditEvents.Add(entry);

        var save = () => db.SaveChangesAsync(CancellationToken.None);

        await save.Should().ThrowAsync<BusinessConflictException>();
        db.ChangeTracker.Clear();
        (await db.AuditEvents.AnyAsync(item => item.EventId == entry.EventId)).Should().BeFalse();
    }

    private sealed class FixedTimeProvider(DateTimeOffset at) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => at;
    }
}
