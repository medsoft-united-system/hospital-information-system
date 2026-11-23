using System.Text.Json;
using FluentAssertions;
using HospitalInformationSystem.Application.Audit.Services;
using HospitalInformationSystem.Application.Models.Audit;
using HospitalInformationSystem.Application.Models.Common;
using HospitalInformationSystem.Application.Models.Security;
using HospitalInformationSystem.Infrastructure.Audit;
using HospitalInformationSystem.Infrastructure.Contexts;
using Microsoft.EntityFrameworkCore;

namespace HospitalInformationSystem.UnitTests;

public sealed class AuditEventFactoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 14, 45, 12, TimeSpan.Zero);
    private readonly AuditEventFactory _factory = new(new FixedTimeProvider(Now));

    [Fact]
    public void Create_AuthenticatedActorAndAccountMutation_ShouldKeepIdentityAndSafeMetadata()
    {
        var actorId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var actor = new AuditActor(AuditActorKind.User, actorId, "admin", "request-12", null);

        var entry = _factory.Create(AuditEventType.AccountChanged, "Пароль аккаунта сброшен.", actor,
            new AuditMetadata
            {
                Action = "password-reset",
                TargetId = targetId,
                AccountId = targetId,
                RevokedRefreshTokenCount = 0,
                RevokeReason = "password-reset"
            });

        entry.EventId.Should().NotBeEmpty();
        entry.EventTimeUtc.Should().Be(Now);
        entry.ActorKind.Should().Be("user");
        entry.ActorUserId.Should().Be(actorId);
        entry.ActorDisplayName.Should().Be("admin");
        entry.EventType.Should().Be(nameof(AuditEventType.AccountChanged));
        using var metadata = JsonDocument.Parse(entry.MetadataJson);
        metadata.RootElement.GetProperty("targetId").GetGuid().Should().Be(targetId);
        metadata.RootElement.GetProperty("revokedRefreshTokenCount").GetInt32().Should().Be(0);
        metadata.RootElement.GetProperty("revokeReason").GetString().Should().Be("password-reset");
        metadata.RootElement.GetProperty("correlationId").GetString().Should().Be("request-12");
        metadata.RootElement.TryGetProperty("attemptedUserName", out _).Should().BeFalse();
        metadata.RootElement.EnumerateObject().Select(property => property.Name).Should().NotContain(
            new[] { "password", "passwordHash", "totp", "secret", "accessToken", "refreshToken", "content" });
    }

    [Fact]
    public void Create_AnonymousFailedLogin_ShouldKeepAttemptedNameSeparateFromActor()
    {
        var entry = _factory.Create(AuditEventType.LoginFailed, "Вход отклонён.",
            new AuditActor(AuditActorKind.Anonymous, null, "Анонимный пользователь", "login-request", null),
            new AuditMetadata { AttemptedUserName = "unknown", RemoteIp = "127.0.0.1", Outcome = "invalid-credentials" });

        entry.ActorKind.Should().Be("anonymous");
        entry.ActorUserId.Should().BeNull();
        entry.ActorDisplayName.Should().NotBe("unknown");
        using var metadata = JsonDocument.Parse(entry.MetadataJson);
        metadata.RootElement.GetProperty("attemptedUserName").GetString().Should().Be("unknown");
    }

    [Fact]
    public void Create_SystemActor_ShouldIdentifyComponentAndGenerateDifferentEventIds()
    {
        var actor = new SystemAuditActorAccessor().GetCurrentActor();
        var first = _factory.Create(AuditEventType.AdministratorSetup, "Первоначальная настройка завершена.", actor);
        var second = _factory.Create(AuditEventType.AdministratorSetup, "Первоначальная настройка завершена.", actor);

        first.ActorKind.Should().Be("system");
        first.Component.Should().Be("HospitalInformationSystem");
        first.ActorUserId.Should().BeNull();
        second.EventId.Should().NotBe(first.EventId);
    }

    [Fact]
    public void Create_ServiceActor_ShouldKeepServiceIdentitySeparateFromHumanUser()
    {
        var serviceId = Guid.NewGuid();
        var entry = _factory.Create(AuditEventType.MedicalRecordChanged, "Зарегистрирован результат анализа.",
            new AuditActor(AuditActorKind.ServiceAccount, null, "lis.external", "service-request", null, serviceId),
            new AuditMetadata { Action = "LaboratoryAnalysisResultRecorded", RecordEntryId = Guid.NewGuid() });

        entry.ActorKind.Should().Be("service");
        entry.ActorServiceAccountId.Should().Be(serviceId);
        entry.ActorUserId.Should().BeNull();
        entry.ActorDisplayName.Should().Be("lis.external");
        using var metadata = JsonDocument.Parse(entry.MetadataJson);
        metadata.RootElement.GetProperty("correlationId").GetString().Should().Be("service-request");
        metadata.RootElement.TryGetProperty("content", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(AuditActorKind.ServiceAccount, false, false)]
    [InlineData(AuditActorKind.ServiceAccount, true, true)]
    [InlineData(AuditActorKind.User, true, true)]
    [InlineData(AuditActorKind.Anonymous, false, true)]
    [InlineData(AuditActorKind.System, false, true)]
    public void Create_MixedOrMissingServiceIdentity_ShouldRejectInvalidActorShape(AuditActorKind kind,
        bool hasUser, bool hasService)
    {
        var actor = new AuditActor(kind, hasUser ? Guid.NewGuid() : null, "Actor", "request", "component",
            hasService ? Guid.NewGuid() : null);

        var create = () => _factory.Create(AuditEventType.MedicalRecordChanged, "Изменена ЭМК.", actor);

        create.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void Create_EmptyServiceId_ShouldRejectUnverifiableIdentity()
    {
        var actor = new AuditActor(AuditActorKind.ServiceAccount, null, "lis.external", "request", null, Guid.Empty);

        var create = () => _factory.Create(AuditEventType.MedicalRecordChanged, "Изменена ЭМК.", actor);

        create.Should().Throw<DomainValidationException>();
    }

    [Theory]
    [InlineData(AuditActorKind.User, false, "request", null)]
    [InlineData(AuditActorKind.Anonymous, true, "request", null)]
    [InlineData(AuditActorKind.Anonymous, false, null, null)]
    [InlineData(AuditActorKind.System, false, null, null)]
    public void Create_InvalidActorShape_ShouldRejectUnverifiableIdentity(AuditActorKind kind, bool hasUserId,
        string? requestId, string? component)
    {
        var actor = new AuditActor(kind, hasUserId ? Guid.NewGuid() : null, "Actor", requestId, component);
        var create = () => _factory.Create(AuditEventType.LoginFailed, "Вход отклонён.", actor);

        create.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void Create_InvalidTypeCountOrControlCharacters_ShouldRejectUnsafeEvent()
    {
        var actor = new SystemAuditActorAccessor().GetCurrentActor();
        var unknownType = () => _factory.Create((AuditEventType)999, "Message", actor);
        var negativeCount = () => _factory.Create(AuditEventType.AccountChanged, "Message", actor,
            new AuditMetadata { RevokedRefreshTokenCount = -1 });
        var negativeServiceCount = () => _factory.Create(AuditEventType.AccountChanged, "Message", actor,
            new AuditMetadata { RevokedServiceSessionCount = -1 });
        var forgedLine = () => _factory.Create(AuditEventType.LoginFailed, "Message", actor,
            new AuditMetadata { AttemptedUserName = "attacker\nforged-entry" });

        unknownType.Should().Throw<DomainValidationException>();
        negativeCount.Should().Throw<DomainValidationException>();
        negativeServiceCount.Should().Throw<DomainValidationException>();
        forgedLine.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void Append_UnsavedEvent_ShouldTrackWithoutCommittingBusinessTransaction()
    {
        using var db = NewDbContext();
        var writer = new AuditWriter(new AuditRepository(db), new AuditEntryFactory(new FixedTimeProvider(Now)), new SystemAuditActorAccessor());

        writer.Append(AuditEventType.PatientCreated, "Пациент создан.",
            new AuditMetadata { Action = "patient-create", TargetId = Guid.NewGuid() });

        var tracked = db.ChangeTracker.Entries().Should().ContainSingle().Subject;
        tracked.State.Should().Be(EntityState.Added);
    }

    [Theory]
    [InlineData(0, 50)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    [InlineData(int.MaxValue, 100)]
    public async Task List_InvalidPageBounds_ShouldRejectBeforeQueryingDatabase(int page, int pageSize)
    {
        using var db = NewDbContext();
        var service = new AuditQueryService(new AuditRepository(db));
        var list = () => service.ListAsync(new AuditQuery(page, pageSize), CancellationToken.None);

        await list.Should().ThrowAsync<DomainValidationException>();
    }

    [Fact]
    public async Task List_InvalidTypeOrPeriod_ShouldRejectBeforeQueryingDatabase()
    {
        using var db = NewDbContext();
        var service = new AuditQueryService(new AuditRepository(db));
        var invalidType = () => service.ListAsync(new AuditQuery(EventType: (AuditEventType)999), CancellationToken.None);
        var invalidPeriod = () => service.ListAsync(new AuditQuery(From: Now.AddSeconds(1), To: Now), CancellationToken.None);

        await invalidType.Should().ThrowAsync<DomainValidationException>();
        await invalidPeriod.Should().ThrowAsync<DomainValidationException>();
    }

    private static HospitalDbContext NewDbContext() => new(new DbContextOptionsBuilder<HospitalDbContext>()
        .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused").Options);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
