using HospitalInformationSystem.Application.Models.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HospitalInformationSystem.Infrastructure.Persistence.Configurations;

public sealed class LaboratoryTestDefinitionConfiguration : IEntityTypeConfiguration<LaboratoryTestDefinitionDbo>
{
    public void Configure(EntityTypeBuilder<LaboratoryTestDefinitionDbo> builder)
    {
        builder.ToTable("laboratory_test_definitions", table =>
        {
            table.HasCheckConstraint("ck_laboratory_tests_loinc_code",
                "char_length(loinc_code) BETWEEN 3 AND 32 AND loinc_code ~ '^[0-9]+-[0-9]$'");
            table.HasCheckConstraint("ck_laboratory_tests_display_name",
                "display_name = btrim(display_name) AND char_length(display_name) BETWEEN 3 AND 256");
            table.HasCheckConstraint("ck_laboratory_tests_dates", "updated_at_utc >= created_at_utc");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.LoincCode).HasColumnName("loinc_code").HasMaxLength(32).IsRequired();
        builder.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(256).IsRequired();
        builder.Property(x => x.IsActive).HasColumnName("is_active");
        builder.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc");
        builder.Property<uint>("xmin").IsRowVersion();
        builder.HasIndex(x => x.LoincCode).IsUnique().HasDatabaseName("ux_laboratory_tests_loinc_code");
    }
}
