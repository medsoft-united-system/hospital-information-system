using HospitalInformationSystem.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HospitalInformationSystem.Infrastructure.Persistence.Configurations;

public sealed class ServiceRefreshSessionConfiguration : IEntityTypeConfiguration<ServiceRefreshSessionDbo>
{
    public void Configure(EntityTypeBuilder<ServiceRefreshSessionDbo> builder)
    {
        builder.ToTable("service_refresh_sessions", table =>
        {
            table.HasCheckConstraint("ck_service_refresh_ids",
                "id <> '00000000-0000-0000-0000-000000000000'::uuid AND family_id <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_service_refresh_hash", "token_hash ~ '^[0-9a-f]{64}$'");
            table.HasCheckConstraint("ck_service_refresh_version", "authentication_version > 0");
            table.HasCheckConstraint("ck_service_refresh_dates", """
                expires_at_utc > created_at_utc
                AND (consumed_at_utc IS NULL OR consumed_at_utc >= created_at_utc)
                AND (revoked_at_utc IS NULL OR revoked_at_utc >= created_at_utc)
                """);
            table.HasCheckConstraint("ck_service_refresh_replacement", """
                (consumed_at_utc IS NULL AND replaced_by_session_id IS NULL)
                OR (consumed_at_utc IS NOT NULL AND replaced_by_session_id IS NOT NULL AND replaced_by_session_id <> id)
                """);
            table.HasCheckConstraint("ck_service_refresh_revoke_reason", """
                (revoked_at_utc IS NULL AND revoke_reason IS NULL)
                OR (revoked_at_utc IS NOT NULL AND revoke_reason IS NOT NULL
                    AND char_length(btrim(revoke_reason)) BETWEEN 1 AND 128)
                """);
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.ServiceAccountId).HasColumnName("service_account_id");
        builder.Property(x => x.FamilyId).HasColumnName("family_id");
        builder.Property(x => x.TokenHash).HasColumnName("token_hash").HasMaxLength(64).IsRequired();
        builder.Property(x => x.AuthenticationVersion).HasColumnName("authentication_version");
        builder.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(x => x.ExpiresAtUtc).HasColumnName("expires_at_utc");
        builder.Property(x => x.ConsumedAtUtc).HasColumnName("consumed_at_utc");
        builder.Property(x => x.ReplacedBySessionId).HasColumnName("replaced_by_session_id");
        builder.Property(x => x.RevokedAtUtc).HasColumnName("revoked_at_utc");
        builder.Property(x => x.RevokeReason).HasColumnName("revoke_reason").HasMaxLength(128);
        builder.HasOne<ServiceAccountDbo>().WithMany().HasForeignKey(x => x.ServiceAccountId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasAlternateKey(x => new { x.Id, x.ServiceAccountId, x.FamilyId });
        builder.HasOne<ServiceRefreshSessionDbo>().WithMany()
            .HasForeignKey(x => new { x.ReplacedBySessionId, x.ServiceAccountId, x.FamilyId })
            .HasPrincipalKey(x => new { x.Id, x.ServiceAccountId, x.FamilyId })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.TokenHash).IsUnique();
        builder.HasIndex(x => new { x.ServiceAccountId, x.RevokedAtUtc });
        builder.HasIndex(x => x.FamilyId);
        builder.Property<uint>("xmin").IsRowVersion();
    }
}
