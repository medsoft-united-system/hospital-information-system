using HospitalInformationSystem.Models.Persistence;
using HospitalInformationSystem.Models.ServiceAccounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HospitalInformationSystem.Infrastructure.Persistence.Configurations;

public sealed class ServiceAccountConfiguration : IEntityTypeConfiguration<ServiceAccountDbo>
{
    public void Configure(EntityTypeBuilder<ServiceAccountDbo> builder)
    {
        builder.ToTable("service_accounts", table =>
        {
            table.HasCheckConstraint("ck_service_accounts_name", "name = btrim(name) AND char_length(name) BETWEEN 3 AND 64");
            table.HasCheckConstraint("ck_service_accounts_normalized_name",
                "normalized_name = btrim(normalized_name) AND char_length(normalized_name) BETWEEN 3 AND 128");
            table.HasCheckConstraint("ck_service_accounts_role", "role IN ('LIS', 'HL7Import')");
            table.HasCheckConstraint("ck_service_accounts_api_key_hash", "octet_length(api_key_hash) = 32 OR " +
                "(octet_length(api_key_hash) BETWEEN 90 AND 128 AND " +
                "substring(api_key_hash FROM 1 FOR 15) = convert_to('$argon2id$v=19$', 'UTF8'))");
            table.HasCheckConstraint("ck_service_accounts_authentication_version", "authentication_version > 0");
            table.HasCheckConstraint("ck_service_accounts_dates", "updated_at_utc >= created_at_utc");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(64).IsRequired();
        builder.Property(x => x.NormalizedName).HasColumnName("normalized_name").HasMaxLength(128).IsRequired();
        builder.Property(x => x.Role).HasColumnName("role").HasMaxLength(16)
            .HasConversion(role => ServiceAccountRoleStorage.Write(role), value => ServiceAccountRoleStorage.Read(value)).IsRequired();
        builder.Property(x => x.IsActive).HasColumnName("is_active");
        builder.Property(x => x.ApiKeyHash).HasColumnName("api_key_hash").IsRequired();
        builder.Property(x => x.AuthenticationVersion).HasColumnName("authentication_version");
        builder.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc");
        builder.Property<uint>("xmin").IsRowVersion();
        builder.HasIndex(x => x.NormalizedName).IsUnique().HasDatabaseName("ux_service_accounts_normalized_name");
    }
}
