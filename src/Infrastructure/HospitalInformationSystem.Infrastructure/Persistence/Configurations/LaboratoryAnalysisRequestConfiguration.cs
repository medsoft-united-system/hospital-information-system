using HospitalInformationSystem.Models.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HospitalInformationSystem.Infrastructure.Persistence.Configurations;

public sealed class LaboratoryAnalysisRequestConfiguration : IEntityTypeConfiguration<LaboratoryAnalysisRequestDbo>
{
    public void Configure(EntityTypeBuilder<LaboratoryAnalysisRequestDbo> builder)
    {
        builder.ToTable("laboratory_analysis_requests", table =>
        {
            table.HasCheckConstraint("ck_lab_requests_loinc_code",
                "char_length(loinc_code_snapshot) BETWEEN 3 AND 32 AND loinc_code_snapshot ~ '^[0-9]+-[0-9]$'");
            table.HasCheckConstraint("ck_lab_requests_display_name",
                "display_name_snapshot = btrim(display_name_snapshot) AND char_length(display_name_snapshot) BETWEEN 3 AND 256");
            table.HasCheckConstraint("ck_lab_requests_comment",
                "comment IS NULL OR (comment = btrim(comment) AND char_length(comment) BETWEEN 1 AND 2000)");
            table.HasCheckConstraint("ck_lab_requests_status", "status IN ('active', 'completed')");
        });
        builder.HasKey(x => x.Id);
        builder.HasAlternateKey(x => new { x.Id, x.PatientId }).HasName("ak_lab_requests_id_patient");
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.MedicalRecordId).HasColumnName("medical_record_id");
        builder.Property(x => x.PatientId).HasColumnName("patient_id");
        builder.Property(x => x.RequestedByMedicalWorkerId).HasColumnName("requested_by_medical_worker_id");
        builder.Property(x => x.LaboratoryTestDefinitionId).HasColumnName("laboratory_test_definition_id");
        builder.Property(x => x.LoincCodeSnapshot).HasColumnName("loinc_code_snapshot").HasMaxLength(32).IsRequired();
        builder.Property(x => x.DisplayNameSnapshot).HasColumnName("display_name_snapshot").HasMaxLength(256).IsRequired();
        builder.Property(x => x.Comment).HasColumnName("comment").HasMaxLength(2000);
        builder.Property(x => x.AuthoredAtUtc).HasColumnName("authored_at_utc");
        builder.Property<DateTimeOffset>("AuthoredAtFhirUtc").HasColumnName("authored_at_fhir_utc")
            .HasComputedColumnSql(
                "date_trunc('milliseconds', authored_at_utc AT TIME ZONE 'UTC') AT TIME ZONE 'UTC'", stored: true);
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(16)
            .HasConversion(value => LaboratoryAnalysisStorage.WriteStatus(value),
                value => LaboratoryAnalysisStorage.ReadStatus(value)).IsRequired();
        builder.HasOne<MedicalWorkerDbo>().WithMany().HasForeignKey(x => x.RequestedByMedicalWorkerId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<LaboratoryTestDefinitionDbo>().WithMany().HasForeignKey(x => x.LaboratoryTestDefinitionId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex("Status", "AuthoredAtFhirUtc", "Id").HasDatabaseName("ix_lab_requests_queue");
        builder.HasIndex("AuthoredAtFhirUtc", "Id").HasDatabaseName("ix_lab_requests_fhir_order");
    }
}
