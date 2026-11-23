using HospitalInformationSystem.Application.Models.Persistence;
using HospitalInformationSystem.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HospitalInformationSystem.Infrastructure.Persistence.Configurations;

public sealed class AdministratorTotpVerificationConfiguration : IEntityTypeConfiguration<AdministratorTotpVerificationDbo>
{
    public void Configure(EntityTypeBuilder<AdministratorTotpVerificationDbo> builder)
    {
        builder.ToTable("administrator_totp_verification", table =>
        {
            table.HasCheckConstraint("ck_totp_step", "last_accepted_step >= 0");
            table.HasCheckConstraint("ck_totp_failed_attempts", "failed_attempts >= 0");
        });
        builder.HasKey(state => state.UserId);
        builder.Property(state => state.UserId).HasColumnName("user_id").ValueGeneratedNever();
        builder.Property(state => state.LastAcceptedStep).HasColumnName("last_accepted_step");
        builder.Property(state => state.FailedAttempts).HasColumnName("failed_attempts");
        builder.Property(state => state.LockedUntilUtc).HasColumnName("locked_until_utc");
        builder.Property<uint>("xmin").IsRowVersion();
        builder.HasOne<HospitalUser>().WithMany().HasForeignKey(state => state.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}
