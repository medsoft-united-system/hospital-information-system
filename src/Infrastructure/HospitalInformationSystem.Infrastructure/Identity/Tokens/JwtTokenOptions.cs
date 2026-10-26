namespace HospitalInformationSystem.Infrastructure.Identity.Tokens;

public sealed class JwtTokenOptions
{
    public string Issuer { get; set; } = "MedSoft.Api";
    public string Audience { get; set; } = "MedSoft.Web";
    public string KeyId { get; set; } = "medsoft-signing-1";
    public string? PrivateKeyPath { get; set; }
    public string? PublicKeyPath { get; set; }
    public int AccessTokenMinutes { get; set; } = 5;
    public int RefreshTokenDays { get; set; } = 7;
    public bool AllowEphemeralKeys { get; set; }
}
