namespace HospitalInformationSystem.Models.Security;

public sealed class AdministratorSetupOptions
{
    public string TokenSha256 { get; set; } = string.Empty;
    public string AdministratorUserName { get; set; } = "admin";
    public TimeSpan ChallengeLifetime { get; set; } = TimeSpan.FromMinutes(5);
    public int MaxChallengeAttempts { get; set; } = 5;
    public int MaxTotpAttempts { get; set; } = 5;
    public TimeSpan TotpLockout { get; set; } = TimeSpan.FromMinutes(5);
    public int AllowedTotpWindow { get; set; } = 1;
    public string Issuer { get; set; } = "MedSoft";
}
