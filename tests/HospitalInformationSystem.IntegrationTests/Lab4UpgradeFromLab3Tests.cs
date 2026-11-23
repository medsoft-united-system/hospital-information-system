using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using HospitalInformationSystem.Application.MedicalRecords.Snapshot;
using HospitalInformationSystem.Application.Models.MedicalRecords;
using HospitalInformationSystem.Application.Models.Persistence;
using HospitalInformationSystem.Application.Models.ServiceAccounts;
using HospitalInformationSystem.Infrastructure.Contexts;
using HospitalInformationSystem.Infrastructure.Identity;
using HospitalInformationSystem.Infrastructure.Persistence.Repositories;
using HospitalInformationSystem.Infrastructure.ServiceAccounts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Testcontainers.PostgreSql;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class Lab4UpgradeFromLab3Tests
{
    private const string Lab3Migration = "20261113142519_UseArgon2idForServiceApiKeys";
    private static readonly DateTimeOffset At = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] LegacyTables =
    [
        "AspNetUsers", "AspNetRoles", "AspNetUserRoles", "administrator_setup_state",
        "administrator_totp_verification", "refresh_sessions", "service_accounts", "service_refresh_sessions",
        "medical_workers", "patients", "medical_records", "diagnosis_definitions", "medical_record_diagnoses",
        "medical_record_history_events", "laboratory_test_definitions", "laboratory_analysis_requests",
        "laboratory_analysis_results", "security_audit_events"
    ];

    [Fact]
    public async Task UpgradeFromLastLab3MigrationTwice_ShouldPreserveAdministratorLisSessionsClinicalDataAndHistory()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("hospital_lab3_to_lab4_upgrade").WithUsername("postgres").WithPassword("postgres").Build();
        await postgres.StartAsync();
        await using var db = new HospitalDbContext(new DbContextOptionsBuilder<HospitalDbContext>()
            .UseNpgsql(postgres.GetConnectionString()).Options);
        await db.GetService<IMigrator>().MigrateAsync(Lab3Migration);
        (await db.Database.GetAppliedMigrationsAsync()).Last().Should().Be(Lab3Migration);
        (await db.Database.SqlQueryRaw<int>("""
            SELECT count(*)::integer AS "Value" FROM information_schema.tables
            WHERE table_schema = current_schema() AND table_name = 'patient_external_identifiers'
            """).SingleAsync()).Should().Be(0);

        var ids = new LegacyIds();
        const string password = "Lab4.Upgrade.only.Password-2026!";
        var passwordHasher = new Argon2idPasswordHasher();
        var passwordHash = passwordHasher.HashPassword(new HospitalUser(), password);
        var stamp = Guid.NewGuid().ToString("N");
        var protector = new EphemeralDataProtectionProvider().CreateProtector("MedSoft.AdministratorTotp.v1");
        var secret = new TotpCodeService().GenerateSecret();
        var protectedSecret = protector.Protect(secret);
        var keys = new ServiceApiKeyGenerator();
        var apiKey = keys.Generate(ids.Lis);
        var humanRefreshHash = HashRandomToken();
        var serviceRefreshHash = HashRandomToken();
        await SeedAsync(db, ids, passwordHash, stamp, protectedSecret, apiKey.Hash, humanRefreshHash, serviceRefreshHash);
        var before = await ReadLegacyRowsAsync(db);

        for (var launch = 0; launch < 2; launch++)
        {
            await db.Database.MigrateAsync();
            (await db.Database.GetAppliedMigrationsAsync()).Should().Equal(db.Database.GetMigrations());
            db.Database.HasPendingModelChanges().Should().BeFalse();
            var after = await ReadLegacyRowsAsync(db);
            foreach (var table in LegacyTables)
                after[table].Should().Equal(before[table], "existing Lab3 rows in {0} must survive launch {1}", table, launch + 1);
        }

        var administrator = await db.Users.AsNoTracking().SingleAsync();
        administrator.IsAdministrator.Should().BeTrue();
        administrator.IsActive.Should().BeTrue();
        administrator.TwoFactorEnabled.Should().BeTrue();
        administrator.SecurityStamp.Should().Be(stamp);
        passwordHasher.VerifyHashedPassword(administrator, administrator.PasswordHash!, password)
            .Should().Be(PasswordVerificationResult.Success);
        var setup = await db.AdministratorSetupStates.AsNoTracking().SingleAsync();
        setup.IsCompleted.Should().BeTrue();
        setup.AdministratorUserId.Should().Be(ids.Administrator);
        protector.Unprotect(setup.TotpSecretProtected!).Should().Be(secret);
        var totpState = await db.Set<AdministratorTotpVerificationDbo>().AsNoTracking().SingleAsync();
        totpState.LastAcceptedStep.Should().Be(At.ToUnixTimeSeconds() / 30);
        var humanSession = await db.RefreshSessions.AsNoTracking().SingleAsync();
        humanSession.TokenHash.Should().Be(humanRefreshHash);
        humanSession.AuthenticationVersion.Should().Be(stamp);
        humanSession.RevokedAtUtc.Should().BeNull();
        var lis = await db.ServiceAccounts.AsNoTracking().SingleAsync();
        lis.Id.Should().Be(ids.Lis);
        lis.Role.Should().Be(ServiceAccountRole.Lis);
        lis.AuthenticationVersion.Should().Be(7);
        lis.ApiKeyHash.Should().Equal(apiKey.Hash);
        keys.Verify(apiKey.ApiKey, lis.Id, lis.ApiKeyHash).Should().Be(ServiceApiKeyVerificationResult.Success);
        var serviceSession = await db.ServiceRefreshSessions.AsNoTracking().SingleAsync();
        serviceSession.TokenHash.Should().Be(serviceRefreshHash);
        serviceSession.AuthenticationVersion.Should().Be(lis.AuthenticationVersion);
        serviceSession.ConsumedAtUtc.Should().BeNull();
        serviceSession.RevokedAtUtc.Should().BeNull();
        var diagnosis = await db.MedicalRecordDiagnoses.AsNoTracking().SingleAsync();
        diagnosis.AddedByMedicalWorkerId.Should().Be(ids.Worker);
        diagnosis.AddedByServiceAccountId.Should().BeNull();
        diagnosis.DiagnosedAtUtc.Should().BeNull();
        diagnosis.DiagnosedAtOffsetMinutes.Should().BeNull();
        diagnosis.CodingSystem.Should().BeNull();
        diagnosis.DiagnosisType.Should().BeNull();
        (await db.PatientExternalIdentifiers.CountAsync()).Should().Be(0);
        var record = (await new MedicalRecordRepository(db).FindAsync(ids.Record, false, CancellationToken.None))!;
        var snapshot = MedicalRecordSnapshotReplay.Build(record, record.HistoryEvents, At.AddMinutes(4));
        snapshot.Diagnoses.Should().ContainSingle(item => item.Id == ids.Diagnosis && item.Code == "A00.00");
        var request = snapshot.LaboratoryAnalysisRequests.Should().ContainSingle().Subject;
        request.Id.Should().Be(ids.Request);
        request.Result.Should().NotBeNull();
        request.Result!.Id.Should().Be(ids.Result);
        request.Result.Value.Should().Be("5.6");
        request.Result.CreatedByServiceAccountId.Should().Be(ids.Lis);
    }

    private static async Task SeedAsync(HospitalDbContext db, LegacyIds ids, string passwordHash, string stamp,
        string protectedSecret, byte[] apiKeyHash, string humanRefreshHash, string serviceRefreshHash)
    {
        const string auditMetadata = """{"action":"lab3-result"}""";
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO medical_workers (id, first_name, last_name, position, role, is_deleted, created_at_utc, updated_at_utc)
            VALUES ({ids.Worker}, 'Учебный', 'Проверочный', 'Врач', 'doctor', false, {At}, {At});
            INSERT INTO "AspNetRoles" ("Id", "Name", "NormalizedName", "ConcurrencyStamp")
            VALUES ({ids.AdminRole}, 'Administrator', 'ADMINISTRATOR', 'lab3-upgrade-role');
            INSERT INTO "AspNetUsers" ("Id", "UserName", "NormalizedUserName", "PasswordHash", "SecurityStamp",
                "ConcurrencyStamp", "MedicalWorkerId", "IsAdministrator", "IsActive", "CreatedAtUtc", "UpdatedAtUtc",
                "EmailConfirmed", "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount")
            VALUES ({ids.Administrator}, 'upgrade.admin', 'UPGRADE.ADMIN', {passwordHash}, {stamp},
                'lab3-upgrade-admin', NULL, true, true, {At}, {At}, false, false, true, true, 0);
            INSERT INTO "AspNetUserRoles" ("UserId", "RoleId") VALUES ({ids.Administrator}, {ids.AdminRole});
            INSERT INTO administrator_setup_state (id, administrator_user_id, is_completed, totp_secret_protected,
                created_at_utc, updated_at_utc, completed_at_utc)
            VALUES (1, {ids.Administrator}, true, {protectedSecret}, {At}, {At}, {At});
            INSERT INTO administrator_totp_verification (user_id, last_accepted_step, failed_attempts, locked_until_utc)
            VALUES ({ids.Administrator}, {At.ToUnixTimeSeconds() / 30}, 0, NULL);
            INSERT INTO refresh_sessions (id, user_id, family_id, token_hash, authentication_version,
                created_at_utc, expires_at_utc, revoked_at_utc, revoke_reason, replaced_by_session_id)
            VALUES ({ids.HumanSession}, {ids.Administrator}, {ids.HumanFamily}, {humanRefreshHash}, {stamp},
                {At}, {At.AddDays(7)}, NULL, NULL, NULL);
            INSERT INTO service_accounts (id, name, normalized_name, role, is_active, api_key_hash,
                authentication_version, created_at_utc, updated_at_utc)
            VALUES ({ids.Lis}, 'upgrade-lis', 'UPGRADE-LIS', 'LIS', true, {apiKeyHash}, 7, {At}, {At});
            INSERT INTO service_refresh_sessions (id, service_account_id, family_id, token_hash,
                authentication_version, created_at_utc, expires_at_utc)
            VALUES ({ids.ServiceSession}, {ids.Lis}, {ids.ServiceFamily}, {serviceRefreshHash}, 7, {At}, {At.AddDays(7)});
            INSERT INTO patients (id, first_name, last_name, birth_date, sex, comment, created_at_utc, updated_at_utc)
            VALUES ({ids.Patient}, 'Учебный', 'Пациент', DATE '1990-01-01', 1, 'Данные лабораторной 3', {At}, {At});
            INSERT INTO medical_records (id, patient_id, created_at_utc, version)
            VALUES ({ids.Record}, {ids.Patient}, {At}, 4);
            INSERT INTO diagnosis_definitions (id, code, name, created_at_utc, updated_at_utc)
            VALUES ({ids.Definition}, 'A00.00', 'Холера', {At}, {At});
            INSERT INTO medical_record_diagnoses (id, medical_record_id, diagnosis_definition_id, diagnosis_code_snapshot,
                diagnosis_name_snapshot, added_at_utc, added_by_medical_worker_id)
            VALUES ({ids.Diagnosis}, {ids.Record}, {ids.Definition}, 'A00.00', 'Холера', {At.AddMinutes(1)}, {ids.Worker});
            INSERT INTO laboratory_test_definitions (id, loinc_code, display_name, is_active, created_at_utc, updated_at_utc)
            VALUES ({ids.Test}, '2345-7', 'Глюкоза в крови', true, {At}, {At});
            INSERT INTO laboratory_analysis_requests (id, medical_record_id, patient_id, requested_by_medical_worker_id,
                laboratory_test_definition_id, loinc_code_snapshot, display_name_snapshot, comment, authored_at_utc, status)
            VALUES ({ids.Request}, {ids.Record}, {ids.Patient}, {ids.Worker}, {ids.Test}, '2345-7', 'Глюкоза в крови',
                'До обновления HIS', {At.AddMinutes(2)}, 'completed');
            INSERT INTO laboratory_analysis_results (id, laboratory_analysis_request_id, patient_id, value,
                issued_at_utc, received_at_utc, created_by_service_account_id)
            VALUES ({ids.Result}, {ids.Request}, {ids.Patient}, '5.6', {At.AddMinutes(3)}, {At.AddMinutes(3)}, {ids.Lis});
            INSERT INTO security_audit_events (event_id, event_time_utc, event_type, actor_kind, actor_service_account_id,
                actor_display_name, request_id, message, metadata)
            VALUES ({ids.Audit}, {At.AddMinutes(3)}, 'MedicalRecordChanged', 'service', {ids.Lis}, 'upgrade-lis',
                'lab3-before-upgrade', 'Результат анализа зарегистрирован до обновления.', {auditMetadata}::jsonb);
            """);
        var events = new (string Type, Guid EntityId, object Payload)[]
        {
            ("MedicalRecordCreated", ids.Record, new { PatientId = ids.Patient }),
            ("DiagnosisAdded", ids.Diagnosis, new { MedicalRecordDiagnosisId = ids.Diagnosis,
                DiagnosisDefinitionId = ids.Definition, Code = "A00.00", Name = "Холера" }),
            ("LaboratoryAnalysisRequested", ids.Request, new LaboratoryAnalysisRequestedPayload(ids.Request,
                ids.Record, ids.Patient, ids.Worker, ids.Test, "2345-7", "Глюкоза в крови", "До обновления HIS", At.AddMinutes(2))),
            ("LaboratoryAnalysisResultRecorded", ids.Result, new LaboratoryAnalysisResultRecordedPayload(ids.Request,
                ids.Result, ids.Patient, "2345-7", "5.6", At.AddMinutes(3), At.AddMinutes(3), ids.Lis))
        };
        for (var index = 0; index < events.Length; index++)
        {
            var item = events[index];
            var serviceAuthor = index == 3;
            var payload = JsonSerializer.Serialize(item.Payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO medical_record_history_events (id, medical_record_id, version, occurred_at_utc, author_kind,
                    performed_by_medical_worker_id, performed_by_service_account_id, performed_by_name_snapshot,
                    performed_by_position_snapshot, event_type, entity_id, payload_json)
                VALUES ({Guid.NewGuid()}, {ids.Record}, {index + 1}, {At.AddMinutes(index)},
                    {(serviceAuthor ? "ServiceAccount" : "MedicalWorker")},
                    {(serviceAuthor ? (Guid?)null : ids.Worker)}, {(serviceAuthor ? ids.Lis : (Guid?)null)},
                    {(serviceAuthor ? "upgrade-lis" : "Проверочный Учебный")},
                    {(serviceAuthor ? "" : "Врач")}, {item.Type}, {item.EntityId}, {payload}::jsonb)
                """);
        }
        await transaction.CommitAsync();
    }

    private static string HashRandomToken() => Convert.ToHexStringLower(SHA256.HashData(RandomNumberGenerator.GetBytes(32)));

    private static async Task<Dictionary<string, string[]>> ReadLegacyRowsAsync(HospitalDbContext db)
    {
        var rows = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var table in LegacyTables)
        {
            var sql = "SELECT (to_jsonb(t) - 'added_by_service_account_id' - 'coding_system'" +
                " - 'diagnosed_at_offset_minutes' - 'diagnosed_at_utc' - 'diagnosis_type')::text AS \"Value\" FROM \"" + table + "\" AS t";
            rows[table] = (await db.Database.SqlQueryRaw<string>(sql).ToArrayAsync()).Order(StringComparer.Ordinal).ToArray();
        }
        return rows;
    }

    private sealed class LegacyIds
    {
        public Guid Administrator { get; } = Guid.NewGuid();
        public Guid AdminRole { get; } = Guid.NewGuid();
        public Guid Worker { get; } = Guid.NewGuid();
        public Guid Lis { get; } = Guid.NewGuid();
        public Guid HumanSession { get; } = Guid.NewGuid();
        public Guid HumanFamily { get; } = Guid.NewGuid();
        public Guid ServiceSession { get; } = Guid.NewGuid();
        public Guid ServiceFamily { get; } = Guid.NewGuid();
        public Guid Patient { get; } = Guid.NewGuid();
        public Guid Record { get; } = Guid.NewGuid();
        public Guid Definition { get; } = Guid.NewGuid();
        public Guid Diagnosis { get; } = Guid.NewGuid();
        public Guid Test { get; } = Guid.NewGuid();
        public Guid Request { get; } = Guid.NewGuid();
        public Guid Result { get; } = Guid.NewGuid();
        public Guid Audit { get; } = Guid.NewGuid();
    }
}
