using System.Text.Json;
using FluentAssertions;
using HospitalInformationSystem.Application.Abstractions.UnitOfWork;
using HospitalInformationSystem.Application.Contracts.Audit;
using HospitalInformationSystem.Application.Hl7Import;
using HospitalInformationSystem.Application.Models.Audit;
using HospitalInformationSystem.Application.Models.Hl7Import;
using HospitalInformationSystem.Application.Models.Security;
using HospitalInformationSystem.Application.Models.ServiceAccounts;
using HospitalInformationSystem.Infrastructure.Audit;
using HospitalInformationSystem.Infrastructure.Hl7Import;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HospitalInformationSystem.UnitTests;

public sealed class Lab4Hl7FailureAuditorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 34, 56, TimeSpan.Zero);
    private static readonly ImportClientCertificate Certificate = new(new string('A', 64),
        "CN=certificate-name", "CN=MedSoft test CA", "1234ABCD", Now.AddDays(-1), Now.AddDays(30));
    private static readonly ServiceAccountIdentity Identity = new(Guid.NewGuid(), "actual.hl7",
        5, ServiceAccountRole.Hl7Import);

    [Theory]
    [InlineData(Hl7ImportAuditOutcomes.AuthenticationRejected)]
    [InlineData(Hl7ImportAuditOutcomes.ProtocolRejected)]
    [InlineData(Hl7ImportAuditOutcomes.ValidationFailed)]
    [InlineData(Hl7ImportAuditOutcomes.InternalError)]
    public async Task Record_SupportedFailure_ShouldPersistServiceActorAndCertificate(string outcome)
    {
        using var harness = new Harness();

        var recorded = await harness.Auditor.RecordAsync(Failure(outcome), CancellationToken.None);

        recorded.Should().BeTrue();
        var state = harness.States.Should().ContainSingle().Subject;
        state.SaveCalls.Should().Be(1);
        state.Disposed.Should().BeTrue();
        var entry = state.Entries.Should().ContainSingle().Subject;
        entry.EventType.Should().Be(AuditEventType.Hl7PatientImported);
        entry.Actor!.Kind.Should().Be(AuditActorKind.ServiceAccount);
        entry.Actor.ServiceAccountId.Should().Be(Identity.Id);
        entry.Actor.DisplayName.Should().Be(Identity.Name);
        entry.Actor.DisplayName.Should().NotBe(Certificate.Subject);
        entry.Actor.UserId.Should().BeNull();
        entry.Actor.RequestId.Should().Be("hl7-audit-001");
        entry.Actor.Component.Should().Be("hl7-import");
        var metadata = entry.Metadata!;
        metadata.Action.Should().Be("hl7-patient-import");
        metadata.Outcome.Should().Be(outcome);
        metadata.ServiceAccountId.Should().Be(Identity.Id);
        metadata.SourceSystem.Should().Be("LAB4_XML");
        metadata.ExternalPatientId.Should().Be("000042");
        metadata.DiagnosisCount.Should().Be(0);
        metadata.TargetId.Should().BeNull();
        metadata.ClientCertificateSha256.Should().Be(Certificate.Sha256Fingerprint);
        metadata.ClientCertificateSubject.Should().Be(Certificate.Subject);
        metadata.ClientCertificateIssuer.Should().Be(Certificate.Issuer);
        metadata.ClientCertificateSerialNumber.Should().Be(Certificate.SerialNumber);
        metadata.ClientCertificateNotBeforeUtc.Should().Be(Certificate.NotBeforeUtc);
        metadata.ClientCertificateNotAfterUtc.Should().Be(Certificate.NotAfterUtc);
        harness.Logger.Messages.Should().BeEmpty();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("lis")]
    [InlineData("empty-id")]
    [InlineData("invalid-version")]
    [InlineData("invalid-name")]
    public async Task Record_UnverifiedIdentity_ShouldUseAnonymousActorWithoutPatientIdentity(string problem)
    {
        using var harness = new Harness();
        var identity = problem switch
        {
            "missing" => null,
            "lis" => Identity with { Role = ServiceAccountRole.Lis },
            "empty-id" => Identity with { Id = Guid.Empty },
            "invalid-version" => Identity with { AuthenticationVersion = 0 },
            "invalid-name" => Identity with { Name = "spoofed\nactor" },
            _ => throw new ArgumentOutOfRangeException(nameof(problem))
        };

        var recorded = await harness.Auditor.RecordAsync(
            Failure() with { ServiceAccount = identity }, CancellationToken.None);

        recorded.Should().BeTrue();
        var entry = harness.States.Single().Entries.Single();
        entry.Actor!.Kind.Should().Be(AuditActorKind.Anonymous);
        entry.Actor.UserId.Should().BeNull();
        entry.Actor.ServiceAccountId.Should().BeNull();
        entry.Actor.RequestId.Should().Be("hl7-audit-001");
        entry.Actor.DisplayName.Should().Be("Неаутентифицированный запрос HL7");
        entry.Actor.DisplayName.Should().NotBe(Certificate.Subject);
        entry.Metadata!.ServiceAccountId.Should().BeNull();
        entry.Metadata.ExternalPatientId.Should().BeNull();
        entry.Metadata.ClientCertificateSha256.Should().Be(Certificate.Sha256Fingerprint);
    }

    [Fact]
    public async Task Record_ShouldUseNewScopeAndLeaveExistingUnitOfWorkUntouched()
    {
        using var harness = new Harness();
        using var existingScope = harness.Services.CreateScope();
        var existing = existingScope.ServiceProvider.GetRequiredService<AuditState>();
        existing.Append(AuditEventType.PatientCreated, "Pending operation.");

        await harness.Auditor.RecordAsync(Failure(), CancellationToken.None);

        harness.States.Should().HaveCount(2);
        existing.SaveCalls.Should().Be(0);
        existing.Disposed.Should().BeFalse();
        existing.Entries.Should().ContainSingle().Which.Message.Should().Be("Pending operation.");
        var isolated = harness.States.Single(state => !ReferenceEquals(state, existing));
        isolated.SaveCalls.Should().Be(1);
        isolated.Disposed.Should().BeTrue();
        isolated.Entries.Should().ContainSingle().Which.EventType.Should().Be(AuditEventType.Hl7PatientImported);
    }

    [Theory]
    [InlineData("append")]
    [InlineData("save")]
    [InlineData("empty-save")]
    public async Task Record_PersistenceFailure_ShouldReturnFalseAndLogOnlyFixedMessage(string problem)
    {
        const string secret = "UAC|Never.Log-Access-Token-Only.6249!";
        using var harness = new Harness(problem, secret);

        var recorded = await harness.Auditor.RecordAsync(Failure(), CancellationToken.None);

        recorded.Should().BeFalse();
        harness.States.Should().ContainSingle().Which.Disposed.Should().BeTrue();
        harness.Logger.Messages.Should().ContainSingle().Which.Text.Should().NotContain(secret);
        harness.Logger.Messages.Should().OnlyContain(entry => entry.Exception == null);
    }

    [Fact]
    public async Task Record_CancelledBeforeCall_ShouldAvoidCreatingScope()
    {
        using var harness = new Harness();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var record = () => harness.Auditor.RecordAsync(Failure(), cancellation.Token);

        await record.Should().ThrowAsync<OperationCanceledException>();
        harness.States.Should().BeEmpty();
        harness.Logger.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task Record_CancelledDuringSave_ShouldPropagateAndDisposeScope()
    {
        using var cancellation = new CancellationTokenSource();
        using var harness = new Harness(save: token =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<int>(token);
        });

        var record = () => harness.Auditor.RecordAsync(Failure(), cancellation.Token);

        await record.Should().ThrowAsync<OperationCanceledException>();
        harness.States.Should().ContainSingle().Which.Disposed.Should().BeTrue();
        harness.Logger.Messages.Should().BeEmpty();
    }

    [Theory]
    [InlineData(Hl7ImportAuditOutcomes.Success)]
    [InlineData(Hl7ImportAuditOutcomes.Duplicate)]
    [InlineData("unknown")]
    [InlineData("")]
    public async Task Record_NonFailureOutcome_ShouldAvoidWritingAnEvent(string outcome)
    {
        using var harness = new Harness();

        var recorded = await harness.Auditor.RecordAsync(Failure(outcome), CancellationToken.None);

        recorded.Should().BeFalse();
        harness.States.Should().BeEmpty();
        harness.Logger.Messages.Should().ContainSingle();
    }

    [Theory]
    [InlineData("control-id")]
    [InlineData("fingerprint")]
    [InlineData("validity")]
    public async Task Record_InvalidRequiredMetadata_ShouldAvoidFalseCertificateAudit(string problem)
    {
        using var harness = new Harness();
        var failure = problem switch
        {
            "control-id" => Failure() with { MessageControlId = "invalid\ncontrol" },
            "fingerprint" => Failure() with { ClientCertificate = Certificate with { Sha256Fingerprint = "invalid" } },
            "validity" => Failure() with { ClientCertificate = Certificate with { NotAfterUtc = Certificate.NotBeforeUtc } },
            _ => throw new ArgumentOutOfRangeException(nameof(problem))
        };

        var recorded = await harness.Auditor.RecordAsync(failure, CancellationToken.None);

        recorded.Should().BeFalse();
        harness.States.Should().BeEmpty();
    }

    [Fact]
    public async Task Record_UnsafeOptionalMetadata_ShouldDropFieldsAndKeepKnownCertificateIdentity()
    {
        using var harness = new Harness();
        var failure = Failure() with
        {
            SourceSystem = "OTHER",
            ExternalPatientId = "invalid\nexternal",
            ClientCertificate = Certificate with
            {
                Subject = new string('S', 2049),
                Issuer = "invalid\nissuer",
                SerialNumber = ""
            }
        };

        await harness.Auditor.RecordAsync(failure, CancellationToken.None);

        var metadata = harness.States.Single().Entries.Single().Metadata!;
        metadata.SourceSystem.Should().BeNull();
        metadata.ExternalPatientId.Should().BeNull();
        metadata.ClientCertificateSubject.Should().BeNull();
        metadata.ClientCertificateIssuer.Should().BeNull();
        metadata.ClientCertificateSerialNumber.Should().BeNull();
        metadata.ClientCertificateSha256.Should().Be(Certificate.Sha256Fingerprint);
        metadata.ClientCertificateNotAfterUtc.Should().Be(Certificate.NotAfterUtc);
    }

    [Fact]
    public async Task Record_MetadataAndMessage_ShouldContainOnlySelectedAuditFields()
    {
        using var harness = new Harness();
        await harness.Auditor.RecordAsync(Failure(), CancellationToken.None);
        var recorded = harness.States.Single().Entries.Single();
        var entry = new AuditEventFactory(new FixedClock()).Create(
            recorded.EventType, recorded.Message, recorded.Actor!, recorded.Metadata);
        using var json = JsonDocument.Parse(entry.MetadataJson);

        json.RootElement.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(
            "action", "serviceAccountId", "correlationId", "outcome", "sourceSystem", "externalPatientId",
            "diagnosisCount", "clientCertificateSha256", "clientCertificateSubject", "clientCertificateIssuer",
            "clientCertificateSerialNumber", "clientCertificateNotBeforeUtc", "clientCertificateNotAfterUtc");
        json.RootElement.GetProperty("correlationId").GetString().Should().Be("hl7-audit-001");
        entry.Message.Should().Be("Импорт пациента через HL7 отклонён.");
    }

    private static Hl7ImportFailure Failure(string outcome = Hl7ImportAuditOutcomes.ValidationFailed) =>
        new("hl7-audit-001", outcome, Certificate, Identity, "LAB4_XML", "000042");

    private sealed class Harness : IDisposable
    {
        public List<AuditState> States { get; } = [];
        public RecordingLogger Logger { get; } = new();
        public ServiceProvider Services { get; }
        public Hl7ImportFailureAuditor Auditor { get; }

        public Harness(string? problem = null, string? exceptionMessage = null,
            Func<CancellationToken, Task<int>>? save = null)
        {
            Services = new ServiceCollection()
                .AddScoped(_ =>
                {
                    var state = new AuditState(problem, exceptionMessage, save);
                    States.Add(state);
                    return state;
                })
                .AddScoped<IAuditWriter>(provider => provider.GetRequiredService<AuditState>())
                .AddScoped<IUnitOfWork>(provider => provider.GetRequiredService<AuditState>())
                .BuildServiceProvider();
            Auditor = new Hl7ImportFailureAuditor(Services.GetRequiredService<IServiceScopeFactory>(), Logger);
        }

        public void Dispose() => Services.Dispose();
    }

    private sealed class AuditState(string? problem, string? exceptionMessage,
        Func<CancellationToken, Task<int>>? save) : IAuditWriter, IUnitOfWork, IDisposable
    {
        public List<CapturedAudit> Entries { get; } = [];
        public int SaveCalls { get; private set; }
        public bool Disposed { get; private set; }

        public void Append(AuditEventType eventType, string message, AuditMetadata? metadata = null,
            AuditActor? actor = null)
        {
            if (problem == "append") throw new InvalidOperationException(exceptionMessage);
            Entries.Add(new CapturedAudit(eventType, message, metadata, actor));
        }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
        {
            SaveCalls++;
            if (problem == "save") throw new InvalidOperationException(exceptionMessage);
            return save is not null ? save(cancellationToken) : Task.FromResult(problem == "empty-save" ? 0 : 1);
        }

        public void Dispose() => Disposed = true;
    }

    private sealed record CapturedAudit(AuditEventType EventType, string Message,
        AuditMetadata? Metadata, AuditActor? Actor);
    private sealed record CapturedLog(string Text, Exception? Exception);

    private sealed class RecordingLogger : ILogger<Hl7ImportFailureAuditor>
    {
        public List<CapturedLog> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(new CapturedLog(formatter(state, exception), exception));
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
