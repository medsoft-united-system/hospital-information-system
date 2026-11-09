using FluentAssertions;
using HospitalInformationSystem.Application.Audit;
using HospitalInformationSystem.Application.Common;
using HospitalInformationSystem.Contracts.Security;
using HospitalInformationSystem.Infrastructure.Audit;
using HospitalInformationSystem.Infrastructure.Contexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class ServiceAuditPersistenceTests(FhirReadApiFactory factory) : IClassFixture<FhirReadApiFactory>
{
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

        var page = await new AuditQueryService(db).ListAsync(new AuditQuery(From: at, To: at), CancellationToken.None);

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
