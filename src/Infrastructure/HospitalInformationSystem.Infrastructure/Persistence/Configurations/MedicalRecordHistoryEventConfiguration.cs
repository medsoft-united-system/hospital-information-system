using HospitalInformationSystem.Models.MedicalRecords;
using HospitalInformationSystem.Models.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HospitalInformationSystem.Infrastructure.Persistence.Configurations;

public sealed class MedicalRecordHistoryEventConfiguration : IEntityTypeConfiguration<MedicalRecordHistoryEventDbo>
{
    public void Configure(EntityTypeBuilder<MedicalRecordHistoryEventDbo> builder)
    {
        builder.ToTable("medical_record_history_events", table =>
        {
            table.HasCheckConstraint("ck_record_history_version", "version > 0");
            table.HasCheckConstraint("ck_record_history_type",
                "event_type IN ('MedicalRecordCreated', 'DiagnosisAdded', 'DiagnosisRemoved', 'PrescriptionAdded', 'PrescriptionCancelled', 'LaboratoryAnalysisRequested', 'LaboratoryAnalysisResultRecorded')");
            table.HasCheckConstraint("ck_record_history_author",
                "(author_kind = 'MedicalWorker' AND performed_by_medical_worker_id IS NOT NULL AND performed_by_service_account_id IS NULL AND event_type <> 'LaboratoryAnalysisResultRecorded') OR " +
                "(author_kind = 'ServiceAccount' AND performed_by_medical_worker_id IS NULL AND performed_by_service_account_id IS NOT NULL AND event_type IN ('MedicalRecordCreated', 'DiagnosisAdded', 'LaboratoryAnalysisResultRecorded'))");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.MedicalRecordId).HasColumnName("medical_record_id");
        builder.Property(x => x.Version).HasColumnName("version");
        builder.Property(x => x.OccurredAtUtc).HasColumnName("occurred_at_utc");
        builder.Property(x => x.AuthorKind).HasColumnName("author_kind").HasMaxLength(16)
            .HasConversion(value => LaboratoryAnalysisStorage.WriteAuthorKind(value),
                value => LaboratoryAnalysisStorage.ReadAuthorKind(value))
            .HasDefaultValue(MedicalRecordAuthorKind.MedicalWorker).IsRequired();
        builder.Property(x => x.PerformedByMedicalWorkerId).HasColumnName("performed_by_medical_worker_id");
        builder.Property(x => x.PerformedByServiceAccountId).HasColumnName("performed_by_service_account_id");
        builder.Property(x => x.PerformedByNameSnapshot).HasColumnName("performed_by_name_snapshot").HasMaxLength(64)
            .IsRequired();
        builder.Property(x => x.PerformedByPositionSnapshot).HasColumnName("performed_by_position_snapshot")
            .HasMaxLength(24).IsRequired();
        builder.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(40).IsRequired();
        builder.Property(x => x.EntityId).HasColumnName("entity_id");
        builder.Property(x => x.PayloadJson).HasColumnName("payload_json").HasColumnType("jsonb").IsRequired();
        builder.HasOne<MedicalWorkerDbo>().WithMany().HasForeignKey(x => x.PerformedByMedicalWorkerId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ServiceAccountDbo>().WithMany().HasForeignKey(x => x.PerformedByServiceAccountId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.MedicalRecordId, x.Version }).IsUnique()
            .HasDatabaseName("ux_record_history_version");
    }
}
