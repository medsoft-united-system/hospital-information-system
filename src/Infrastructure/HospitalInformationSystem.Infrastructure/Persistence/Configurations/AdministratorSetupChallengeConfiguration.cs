using HospitalInformationSystem.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HospitalInformationSystem.Infrastructure.Persistence.Configurations;

public sealed class AdministratorSetupChallengeConfiguration : IEntityTypeConfiguration<AdministratorSetupChallengeDbo>
{
    public void Configure(EntityTypeBuilder<AdministratorSetupChallengeDbo> builder)
    {
        builder.ToTable("administrator_setup_challenges", table =>
        {
            table.HasCheckConstraint("ck_setup_challenge_dates", "expires_at_utc > created_at_utc");
            table.HasCheckConstraint("ck_setup_challenge_attempts", "failed_attempts >= 0");
        });
        builder.HasKey(challenge => challenge.ChallengeId);
        builder.Property(challenge => challenge.ChallengeId).HasColumnName("challenge_id").ValueGeneratedNever();
        builder.Property(challenge => challenge.AdministratorUserId).HasColumnName("administrator_user_id");
        builder.Property(challenge => challenge.UserName).HasColumnName("user_name").HasMaxLength(16).IsRequired();
        builder.Property(challenge => challenge.PasswordHash).HasColumnName("password_hash").HasMaxLength(1024).IsRequired();
        builder.Property(challenge => challenge.TotpSecretProtected).HasColumnName("totp_secret_protected").HasMaxLength(4096).IsRequired();
        builder.Property(challenge => challenge.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(challenge => challenge.ExpiresAtUtc).HasColumnName("expires_at_utc");
        builder.Property(challenge => challenge.FailedAttempts).HasColumnName("failed_attempts");
        builder.Property<uint>("xmin").IsRowVersion();
        builder.HasIndex(challenge => challenge.ExpiresAtUtc);
    }
}
