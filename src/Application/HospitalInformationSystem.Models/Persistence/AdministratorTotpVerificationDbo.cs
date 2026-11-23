namespace HospitalInformationSystem.Models.Persistence;

public sealed class AdministratorTotpVerificationDbo
{
    public Guid UserId { get; set; }
    public long LastAcceptedStep { get; set; }
    public int FailedAttempts { get; set; }
    public DateTimeOffset? LockedUntilUtc { get; set; }
}
