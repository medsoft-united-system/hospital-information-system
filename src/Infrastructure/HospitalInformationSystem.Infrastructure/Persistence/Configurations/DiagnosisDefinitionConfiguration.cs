using HospitalInformationSystem.Application.Models.Diagnoses;
using HospitalInformationSystem.Application.Models.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HospitalInformationSystem.Infrastructure.Persistence.Configurations;

public sealed class DiagnosisDefinitionConfiguration : IEntityTypeConfiguration<DiagnosisDefinitionDbo>
{
    public void Configure(EntityTypeBuilder<DiagnosisDefinitionDbo> builder)
    {
        builder.ToTable("diagnosis_definitions", table =>
        {
            table.HasCheckConstraint("ck_diagnosis_definitions_code",
                $"char_length(btrim(code)) BETWEEN {DiagnosisLimits.MinCodeLength} AND {DiagnosisLimits.MaxCodeLength}");
            table.HasCheckConstraint("ck_diagnosis_definitions_name",
                $"char_length(btrim(name)) BETWEEN {DiagnosisLimits.MinNameLength} AND {DiagnosisLimits.MaxNameLength}");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.Code).HasColumnName("code").HasMaxLength(DiagnosisLimits.MaxCodeLength).IsRequired();
        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(DiagnosisLimits.MaxNameLength).IsRequired();
        builder.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc");
        builder.Property<uint>("xmin").IsRowVersion();
        builder.HasIndex(x => x.Code).IsUnique().HasDatabaseName("ux_diagnosis_definitions_code");
        builder.HasIndex(x => x.Name).IsUnique().HasDatabaseName("ux_diagnosis_definitions_name");
    }
}
