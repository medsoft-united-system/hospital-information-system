namespace HospitalInformationSystem.Application.Models.Persistence;

public sealed class AdministratorSetupChallengeDbo
{
    public Guid ChallengeId { get; set; }
    public Guid AdministratorUserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string TotpSecretProtected { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public int FailedAttempts { get; set; }
}
