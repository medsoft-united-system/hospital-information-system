using HospitalInformationSystem.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HospitalInformationSystem.Infrastructure.Persistence.Configurations;

public sealed class PatientExternalIdentifierConfiguration : IEntityTypeConfiguration<PatientExternalIdentifierDbo>
{
    public void Configure(EntityTypeBuilder<PatientExternalIdentifierDbo> builder)
    {
        builder.ToTable("patient_external_identifiers", table =>
        {
            table.HasCheckConstraint("ck_patient_external_identifiers_source",
                "char_length(source_system) BETWEEN 1 AND 64 AND source_system = btrim(source_system)");
            table.HasCheckConstraint("ck_patient_external_identifiers_external_id",
                "char_length(external_patient_id) BETWEEN 1 AND 128 AND external_patient_id = btrim(external_patient_id)");
        });
        builder.HasKey(x => new { x.SourceSystem, x.ExternalPatientId })
            .HasName("pk_patient_external_identifiers");
        builder.Property(x => x.SourceSystem).HasColumnName("source_system").HasMaxLength(64);
        builder.Property(x => x.ExternalPatientId).HasColumnName("external_patient_id").HasMaxLength(128);
        builder.Property(x => x.PatientId).HasColumnName("patient_id");
        builder.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.HasOne<PatientDbo>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.PatientId);
    }
}
