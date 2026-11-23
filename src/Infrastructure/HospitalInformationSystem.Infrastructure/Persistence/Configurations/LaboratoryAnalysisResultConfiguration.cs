using HospitalInformationSystem.Models.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HospitalInformationSystem.Infrastructure.Persistence.Configurations;

public sealed class LaboratoryAnalysisResultConfiguration : IEntityTypeConfiguration<LaboratoryAnalysisResultDbo>
{
    public void Configure(EntityTypeBuilder<LaboratoryAnalysisResultDbo> builder)
    {
        builder.ToTable("laboratory_analysis_results", table =>
        {
            table.HasCheckConstraint("ck_lab_results_distinct_id", "id <> laboratory_analysis_request_id");
            table.HasCheckConstraint("ck_lab_results_value",
                "value = btrim(value) AND char_length(value) BETWEEN 1 AND 2000");
            table.HasCheckConstraint("ck_lab_results_issued_precision",
                "issued_at_utc = date_trunc('milliseconds', issued_at_utc)");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.LaboratoryAnalysisRequestId).HasColumnName("laboratory_analysis_request_id");
        builder.Property(x => x.PatientId).HasColumnName("patient_id");
        builder.Property(x => x.Value).HasColumnName("value").HasMaxLength(2000).IsRequired();
        builder.Property(x => x.IssuedAtUtc).HasColumnName("issued_at_utc");
        builder.Property(x => x.ReceivedAtUtc).HasColumnName("received_at_utc");
        builder.Property(x => x.CreatedByServiceAccountId).HasColumnName("created_by_service_account_id");
        builder.HasOne<LaboratoryAnalysisRequestDbo>().WithOne(x => x.Result)
            .HasForeignKey<LaboratoryAnalysisResultDbo>(x => new { x.LaboratoryAnalysisRequestId, x.PatientId })
            .HasPrincipalKey<LaboratoryAnalysisRequestDbo>(x => new { x.Id, x.PatientId })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ServiceAccountDbo>().WithMany().HasForeignKey(x => x.CreatedByServiceAccountId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.LaboratoryAnalysisRequestId).IsUnique()
            .HasDatabaseName("ux_lab_results_request_id");
        builder.HasIndex(x => new { x.IssuedAtUtc, x.Id }).HasDatabaseName("ix_lab_results_issued_id");
    }
}
