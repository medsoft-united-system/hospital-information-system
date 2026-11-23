namespace HospitalInformationSystem.Models.Security;

public sealed record SetupChallengeDto(
    Guid ChallengeId,
    string SecretKey,
    string AuthenticatorUri,
    DateTimeOffset ExpiresAt);
