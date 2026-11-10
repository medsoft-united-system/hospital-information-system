using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using HospitalInformationSystem.Application.MedicalRecords.Snapshot;
using HospitalInformationSystem.Domain.MedicalRecords;
using HospitalInformationSystem.Infrastructure.Contexts;
using HospitalInformationSystem.Infrastructure.Identity;
using HospitalInformationSystem.Infrastructure.Persistence.Models;
using HospitalInformationSystem.Infrastructure.Persistence.Repositories;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Testcontainers.PostgreSql;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class Lab3UpgradeFromLab2Tests
{
    private const string Lab2Migration = "20261026111742_AddAuthenticationSessionsAndAdministratorSetup";
    private static readonly DateTimeOffset At = new(2026, 10, 27, 10, 0, 0, TimeSpan.Zero);
    private static readonly string[] LegacyTables =
    [
        "AspNetUsers", "AspNetRoles", "AspNetUserRoles", "AspNetUserClaims", "administrator_setup_state",
        "administrator_totp_verification", "refresh_sessions", "security_audit_events", "medical_workers",
        "patients", "medical_records", "diagnosis_definitions", "medical_record_diagnoses", "prescriptions",
        "medical_record_history_events"
    ];

    [Fact]
    public async Task UpgradeFromLastLab2Migration_ShouldPreserveCredentialsSetupSessionsAuditAndClinicalReplay()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("hospital_real_lab2_upgrade").WithUsername("postgres").WithPassword("postgres").Build();
        await postgres.StartAsync();
        await using var db = new HospitalDbContext(new DbContextOptionsBuilder<HospitalDbContext>()
            .UseNpgsql(postgres.GetConnectionString()).Options);
        await db.GetService<IMigrator>().MigrateAsync(Lab2Migration);
        (await db.Database.GetAppliedMigrationsAsync()).Last().Should().Be(Lab2Migration);
        (await db.Database.SqlQueryRaw<int>("""
            SELECT count(*)::integer AS "Value" FROM information_schema.tables
            WHERE table_schema = current_schema() AND table_name IN
                ('service_accounts', 'laboratory_test_definitions', 'laboratory_analysis_requests',
                 'laboratory_analysis_results', 'service_refresh_sessions')
            """).SingleAsync()).Should().Be(0);
        var ids = new LegacyIds();
        const string password = "Migration.only.Password-2026!";
        var hasher = new Argon2idPasswordHasher();
        var passwordHash = hasher.HashPassword(new HospitalUser(), password);
        var securityStamp = Guid.NewGuid().ToString("N");
        var protection = new EphemeralDataProtectionProvider().CreateProtector("MedSoft.AdministratorTotp.v1");
        var totp = new TotpCodeService();
        var secret = totp.GenerateSecret();
        var protectedSecret = protection.Protect(secret);
        var refresh = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var refreshHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(refresh)));
        await SeedIdentityAsync(db, ids, passwordHash, securityStamp, protectedSecret, refreshHash);
        await SeedClinicalAsync(db, ids);
        var before = await ReadLegacyRowsAsync(db);

        await db.Database.MigrateAsync();

        (await db.Database.GetAppliedMigrationsAsync()).Should().Equal(db.Database.GetMigrations());
        db.Database.HasPendingModelChanges().Should().BeFalse();
        var after = await ReadLegacyRowsAsync(db);
        foreach (var table in LegacyTables)
            after[table].SequenceEqual(before[table], StringComparer.Ordinal).Should().BeTrue(
                "all original columns in {0} must survive the full Lab2 → Lab3 migration unchanged", table);
        var administrator = await db.Users.AsNoTracking().SingleAsync(user => user.Id == ids.Administrator);
        administrator.UserName.Should().Be("legacy.admin");
        administrator.SecurityStamp.Should().Be(securityStamp);
        administrator.PasswordHash.Should().Be(passwordHash);
        administrator.IsAdministrator.Should().BeTrue();
        administrator.IsActive.Should().BeTrue();
        administrator.TwoFactorEnabled.Should().BeTrue();
        hasher.VerifyHashedPassword(administrator, administrator.PasswordHash!, password)
            .Should().Be(PasswordVerificationResult.Success);
        var workerAccount = await db.Users.AsNoTracking().SingleAsync(user => user.Id == ids.WorkerAccount);
        workerAccount.MedicalWorkerId.Should().Be(ids.Worker);
        workerAccount.IsAdministrator.Should().BeFalse();
        hasher.VerifyHashedPassword(workerAccount, workerAccount.PasswordHash!, password)
            .Should().Be(PasswordVerificationResult.Success);
        (await db.UserClaims.SingleAsync()).ClaimValue.Should().Be(ids.Worker.ToString("D"));
        var setup = await db.AdministratorSetupStates.AsNoTracking().SingleAsync();
        setup.IsCompleted.Should().BeTrue();
        setup.AdministratorUserId.Should().Be(ids.Administrator);
        setup.CompletedAtUtc.Should().Be(At.AddMinutes(1));
        setup.TotpSecretProtected.Should().Be(protectedSecret);
        var unprotected = protection.Unprotect(setup.TotpSecretProtected!);
        (unprotected == secret).Should().BeTrue();
        totp.GetCode(unprotected, At.AddMinutes(10)).Should().Be(totp.GetCode(secret, At.AddMinutes(10)));
        var verification = await db.Set<AdministratorTotpVerificationDbo>().AsNoTracking().SingleAsync();
        verification.UserId.Should().Be(ids.Administrator);
        verification.LastAcceptedStep.Should().Be(At.ToUnixTimeSeconds() / 30);
        verification.FailedAttempts.Should().Be(2);
        verification.LockedUntilUtc.Should().Be(At.AddMinutes(5));
        var sessions = await db.RefreshSessions.AsNoTracking().ToArrayAsync();
        sessions.Should().HaveCount(2);
        var current = sessions.Single(session => session.Id == ids.ActiveSession);
        current.UserId.Should().Be(ids.WorkerAccount);
        current.FamilyId.Should().Be(ids.Family);
        current.AuthenticationVersion.Should().Be(securityStamp);
        current.TokenHash.Should().Be(refreshHash);
        current.ExpiresAtUtc.Should().Be(At.AddDays(7));
        current.RevokedAtUtc.Should().BeNull();
        var previous = sessions.Single(session => session.Id == ids.ConsumedSession);
        previous.ReplacedBySessionId.Should().Be(ids.ActiveSession);
        previous.RevokedAtUtc.Should().Be(At.AddMinutes(6));
        previous.RevokeReason.Should().Be("rotated");
        var audits = await db.AuditEvents.AsNoTracking().ToArrayAsync();
        audits.Should().HaveCount(2).And.OnlyContain(entry => entry.ActorServiceAccountId == null);
        var humanAudit = audits.Single(entry => entry.EventId == ids.UserAudit);
        humanAudit.ActorKind.Should().Be("user");
        humanAudit.ActorUserId.Should().Be(ids.Administrator);
        var systemAudit = audits.Single(entry => entry.EventId == ids.SystemAudit);
        systemAudit.ActorKind.Should().Be("system");
        systemAudit.Component.Should().Be("LegacySetup");
        var history = await db.MedicalRecordHistoryEvents.AsNoTracking().OrderBy(entry => entry.Version).ToArrayAsync();
        history.Should().HaveCount(5).And.OnlyContain(entry => entry.AuthorKind == MedicalRecordAuthorKind.MedicalWorker &&
            entry.PerformedByMedicalWorkerId == ids.Worker && entry.PerformedByServiceAccountId == null);
        (await db.MedicalRecords.AsNoTracking().SingleAsync()).Version.Should().Be(5);
        var record = (await new MedicalRecordRepository(db).FindAsync(ids.Record, false, CancellationToken.None))!;
        var activeDiagnosis = MedicalRecordSnapshotReplay.Build(record, record.HistoryEvents, At.AddMinutes(1));
        activeDiagnosis.Diagnoses.Should().ContainSingle(diagnosis => diagnosis.Id == ids.Diagnosis && diagnosis.Name == "Холера");
        activeDiagnosis.LaboratoryAnalysisRequests.Should().BeEmpty();
        var activePrescription = MedicalRecordSnapshotReplay.Build(record, record.HistoryEvents, At.AddMinutes(3));
        activePrescription.Diagnoses.Should().BeEmpty();
        activePrescription.Prescriptions.Should().ContainSingle(prescription =>
            prescription.Id == ids.Prescription && prescription.Text == "Контрольный осмотр");
        activePrescription.LaboratoryAnalysisRequests.Should().BeEmpty();
        var cancelled = MedicalRecordSnapshotReplay.Build(record, record.HistoryEvents, At.AddMinutes(4));
        cancelled.Prescriptions.Should().BeEmpty();
        cancelled.LaboratoryAnalysisRequests.Should().BeEmpty();
        (await db.ServiceAccounts.CountAsync()).Should().Be(0);
        (await db.ServiceRefreshSessions.CountAsync()).Should().Be(0);
        (await db.LaboratoryTestDefinitions.CountAsync()).Should().Be(0);
        (await db.LaboratoryAnalysisRequests.CountAsync()).Should().Be(0);
        (await db.LaboratoryAnalysisResults.CountAsync()).Should().Be(0);
    }

    private static async Task SeedIdentityAsync(HospitalDbContext db, LegacyIds ids, string passwordHash,
        string securityStamp, string protectedSecret, string refreshHash)
    {
        var previousHash = Convert.ToHexStringLower(SHA256.HashData(RandomNumberGenerator.GetBytes(32)));
        const string userMetadata = """{"action":"legacy-account-change"}""";
        const string setupMetadata = """{"action":"legacy-setup-completed"}""";
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO medical_workers (id, first_name, last_name, position, role, is_deleted, created_at_utc, updated_at_utc)
            VALUES ({ids.Worker}, 'Алексей', 'Иванов', 'Врач-терапевт', 'doctor', false, {At}, {At});
            INSERT INTO "AspNetRoles" ("Id", "Name", "NormalizedName", "ConcurrencyStamp")
            VALUES ({ids.AdminRole}, 'Administrator', 'ADMINISTRATOR', 'legacy-admin-role'),
                ({ids.ClinicalRole}, 'MedicalWorker', 'MEDICALWORKER', 'legacy-medical-role');
            INSERT INTO "AspNetUsers" ("Id", "UserName", "NormalizedUserName", "PasswordHash", "SecurityStamp",
                "ConcurrencyStamp", "MedicalWorkerId", "IsAdministrator", "IsActive", "CreatedAtUtc", "UpdatedAtUtc",
                "EmailConfirmed", "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount")
            VALUES ({ids.Administrator}, 'legacy.admin', 'LEGACY.ADMIN', {passwordHash}, {securityStamp},
                'legacy-admin-concurrency', NULL, false, true, {At}, {At}, false, false, true, true, 0),
                ({ids.WorkerAccount}, 'legacy.doctor', 'LEGACY.DOCTOR', {passwordHash}, {securityStamp},
                'legacy-doctor-concurrency', {ids.Worker}, false, true, {At}, {At}, false, false, false, true, 0);
            INSERT INTO "AspNetUserRoles" ("UserId", "RoleId") VALUES
                ({ids.Administrator}, {ids.AdminRole}), ({ids.WorkerAccount}, {ids.ClinicalRole});
            INSERT INTO "AspNetUserClaims" ("UserId", "ClaimType", "ClaimValue")
            VALUES ({ids.WorkerAccount}, 'medical_worker_id', {ids.Worker.ToString("D")});
            INSERT INTO administrator_setup_state (id, administrator_user_id, is_completed, totp_secret_protected,
                created_at_utc, updated_at_utc, completed_at_utc)
            VALUES (1, {ids.Administrator}, true, {protectedSecret}, {At}, {At.AddMinutes(1)}, {At.AddMinutes(1)});
            INSERT INTO administrator_totp_verification (user_id, last_accepted_step, failed_attempts, locked_until_utc)
            VALUES ({ids.Administrator}, {At.ToUnixTimeSeconds() / 30}, 2, {At.AddMinutes(5)});
            INSERT INTO refresh_sessions (id, user_id, family_id, token_hash, authentication_version,
                created_at_utc, expires_at_utc, revoked_at_utc, revoke_reason, replaced_by_session_id)
            VALUES ({ids.ActiveSession}, {ids.WorkerAccount}, {ids.Family}, {refreshHash}, {securityStamp},
                {At.AddMinutes(6)}, {At.AddDays(7)}, NULL, NULL, NULL);
            INSERT INTO refresh_sessions (id, user_id, family_id, token_hash, authentication_version,
                created_at_utc, expires_at_utc, revoked_at_utc, revoke_reason, replaced_by_session_id)
            VALUES ({ids.ConsumedSession}, {ids.WorkerAccount}, {ids.Family}, {previousHash}, {securityStamp},
                {At}, {At.AddDays(7)}, {At.AddMinutes(6)}, 'rotated', {ids.ActiveSession});
            INSERT INTO security_audit_events (event_id, event_time_utc, event_type, actor_kind, actor_user_id,
                actor_display_name, request_id, component, message, metadata)
            VALUES ({ids.UserAudit}, {At.AddMinutes(2)}, 'AccountChanged', 'user', {ids.Administrator},
                'legacy.admin', 'legacy-account-request', NULL, 'Учётная запись изменена до обновления.',
                {userMetadata}::jsonb),
                ({ids.SystemAudit}, {At.AddMinutes(1)}, 'AdministratorSetup', 'system', NULL,
                'Legacy setup', NULL, 'LegacySetup', 'Первоначальная настройка завершена.',
                {setupMetadata}::jsonb);
            """);
        await transaction.CommitAsync();
    }

    private static async Task SeedClinicalAsync(HospitalDbContext db, LegacyIds ids)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO patients (id, first_name, last_name, birth_date, sex, comment, created_at_utc, updated_at_utc)
            VALUES ({ids.Patient}, 'Иван', 'Петров', DATE '1990-01-01', 1, 'Сведения до обновления', {At}, {At});
            INSERT INTO medical_records (id, patient_id, created_at_utc, version)
            VALUES ({ids.Record}, {ids.Patient}, {At}, 5);
            INSERT INTO diagnosis_definitions (id, code, name, created_at_utc, updated_at_utc)
            VALUES ({ids.Definition}, 'A00.00', 'Холера', {At}, {At});
            INSERT INTO medical_record_diagnoses (id, medical_record_id, diagnosis_definition_id, diagnosis_code_snapshot,
                diagnosis_name_snapshot, added_at_utc, added_by_medical_worker_id, removed_at_utc, removed_by_medical_worker_id)
            VALUES ({ids.Diagnosis}, {ids.Record}, {ids.Definition}, 'A00.00', 'Холера', {At.AddMinutes(1)},
                {ids.Worker}, {At.AddMinutes(2)}, {ids.Worker});
            INSERT INTO prescriptions (id, medical_record_id, text, created_at_utc, created_by_medical_worker_id,
                cancelled_at_utc, cancelled_by_medical_worker_id)
            VALUES ({ids.Prescription}, {ids.Record}, 'Контрольный осмотр', {At.AddMinutes(3)}, {ids.Worker},
                {At.AddMinutes(4)}, {ids.Worker});
            """);
        var events = new (string Type, Guid? EntityId, object Payload)[]
        {
            ("MedicalRecordCreated", ids.Record, new { PatientId = ids.Patient }),
            ("DiagnosisAdded", ids.Diagnosis, new { MedicalRecordDiagnosisId = ids.Diagnosis, DiagnosisDefinitionId = ids.Definition, Code = "A00.00", Name = "Холера" }),
            ("DiagnosisRemoved", ids.Diagnosis, new { MedicalRecordDiagnosisId = ids.Diagnosis, Code = "A00.00", Name = "Холера" }),
            ("PrescriptionAdded", ids.Prescription, new { PrescriptionId = ids.Prescription, Text = "Контрольный осмотр", ReplacesPrescriptionId = (Guid?)null }),
            ("PrescriptionCancelled", ids.Prescription, new { PrescriptionId = ids.Prescription, Text = "Контрольный осмотр" })
        };
        for (var index = 0; index < events.Length; index++)
        {
            var item = events[index];
            var payload = JsonSerializer.Serialize(item.Payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO medical_record_history_events (id, medical_record_id, version, occurred_at_utc,
                    performed_by_medical_worker_id, performed_by_name_snapshot, performed_by_position_snapshot,
                    event_type, entity_id, payload_json)
                VALUES ({Guid.NewGuid()}, {ids.Record}, {index + 1}, {At.AddMinutes(index)}, {ids.Worker},
                    'Иванов Алексей', 'Врач-терапевт', {item.Type}, {item.EntityId}, {payload}::jsonb)
                """);
        }
    }

    private static async Task<Dictionary<string, string[]>> ReadLegacyRowsAsync(HospitalDbContext db)
    {
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var table in LegacyTables)
        {
            var sql = "SELECT (to_jsonb(t) - 'author_kind' - 'performed_by_service_account_id' - 'actor_service_account_id')::text AS \"Value\" FROM \"" + table + "\" AS t";
            var rows = await db.Database.SqlQueryRaw<string>(sql).ToArrayAsync();
            result.Add(table, rows.Order(StringComparer.Ordinal).ToArray());
        }
        return result;
    }

    private sealed class LegacyIds
    {
        public Guid Administrator { get; } = Guid.NewGuid();
        public Guid WorkerAccount { get; } = Guid.NewGuid();
        public Guid AdminRole { get; } = Guid.NewGuid();
        public Guid ClinicalRole { get; } = Guid.NewGuid();
        public Guid Worker { get; } = Guid.NewGuid();
        public Guid Patient { get; } = Guid.NewGuid();
        public Guid Record { get; } = Guid.NewGuid();
        public Guid Definition { get; } = Guid.NewGuid();
        public Guid Diagnosis { get; } = Guid.NewGuid();
        public Guid Prescription { get; } = Guid.NewGuid();
        public Guid ActiveSession { get; } = Guid.NewGuid();
        public Guid ConsumedSession { get; } = Guid.NewGuid();
        public Guid Family { get; } = Guid.NewGuid();
        public Guid UserAudit { get; } = Guid.NewGuid();
        public Guid SystemAudit { get; } = Guid.NewGuid();
    }
}
