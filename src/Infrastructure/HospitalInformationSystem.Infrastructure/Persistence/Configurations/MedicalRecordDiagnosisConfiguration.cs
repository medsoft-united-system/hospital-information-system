using HospitalInformationSystem.Models.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HospitalInformationSystem.Infrastructure.Persistence.Configurations;

public sealed class MedicalRecordDiagnosisConfiguration : IEntityTypeConfiguration<MedicalRecordDiagnosisDbo>
{
    public void Configure(EntityTypeBuilder<MedicalRecordDiagnosisDbo> builder)
    {
        builder.ToTable("medical_record_diagnoses", table =>
        {
            table.HasCheckConstraint("ck_record_diagnoses_code", "char_length(btrim(diagnosis_code_snapshot)) BETWEEN 3 AND 32");
            table.HasCheckConstraint("ck_record_diagnoses_name",
                "char_length(btrim(diagnosis_name_snapshot)) BETWEEN 3 AND 256");
            table.HasCheckConstraint("ck_record_diagnoses_author",
                "(added_by_medical_worker_id IS NOT NULL AND added_by_service_account_id IS NULL) OR " +
                "(added_by_medical_worker_id IS NULL AND added_by_service_account_id IS NOT NULL)");
            table.HasCheckConstraint("ck_record_diagnoses_clinical_time",
                "(diagnosed_at_utc IS NULL AND diagnosed_at_offset_minutes IS NULL) OR " +
                "(diagnosed_at_utc IS NOT NULL AND diagnosed_at_offset_minutes IS NOT NULL AND " +
                "diagnosed_at_offset_minutes BETWEEN -840 AND 840)");
            table.HasCheckConstraint("ck_record_diagnoses_coding_system",
                "coding_system IS NULL OR char_length(btrim(coding_system)) BETWEEN 1 AND 32");
            table.HasCheckConstraint("ck_record_diagnoses_diagnosis_type",
                "diagnosis_type IS NULL OR char_length(btrim(diagnosis_type)) BETWEEN 1 AND 32");
            table.HasCheckConstraint("ck_record_diagnoses_service_details",
                "added_by_service_account_id IS NULL OR " +
                "(diagnosed_at_utc IS NOT NULL AND coding_system IS NOT NULL AND diagnosis_type IS NOT NULL)");
            table.HasCheckConstraint("ck_record_diagnoses_removal",
                "(removed_at_utc IS NULL) = (removed_by_medical_worker_id IS NULL)");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.MedicalRecordId).HasColumnName("medical_record_id");
        builder.Property(x => x.DiagnosisDefinitionId).HasColumnName("diagnosis_definition_id");
        builder.Property(x => x.DiagnosisCodeSnapshot).HasColumnName("diagnosis_code_snapshot").HasMaxLength(32)
            .IsRequired();
        builder.Property(x => x.DiagnosisNameSnapshot).HasColumnName("diagnosis_name_snapshot").HasMaxLength(256)
            .IsRequired();
        builder.Property(x => x.AddedAtUtc).HasColumnName("added_at_utc");
        builder.Property(x => x.AddedByMedicalWorkerId).HasColumnName("added_by_medical_worker_id");
        builder.Property(x => x.AddedByServiceAccountId).HasColumnName("added_by_service_account_id");
        builder.Property(x => x.DiagnosedAtUtc).HasColumnName("diagnosed_at_utc");
        builder.Property(x => x.DiagnosedAtOffsetMinutes).HasColumnName("diagnosed_at_offset_minutes");
        builder.Property(x => x.CodingSystem).HasColumnName("coding_system").HasMaxLength(32);
        builder.Property(x => x.DiagnosisType).HasColumnName("diagnosis_type").HasMaxLength(32);
        builder.Property(x => x.RemovedAtUtc).HasColumnName("removed_at_utc");
        builder.Property(x => x.RemovedByMedicalWorkerId).HasColumnName("removed_by_medical_worker_id");
        builder.HasOne<DiagnosisDefinitionDbo>().WithMany().HasForeignKey(x => x.DiagnosisDefinitionId)
            .OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<MedicalWorkerDbo>().WithMany().HasForeignKey(x => x.AddedByMedicalWorkerId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ServiceAccountDbo>().WithMany().HasForeignKey(x => x.AddedByServiceAccountId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<MedicalWorkerDbo>().WithMany().HasForeignKey(x => x.RemovedByMedicalWorkerId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
