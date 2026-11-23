namespace HospitalInformationSystem.Contracts.Abstractions;

public interface IAdministratorSetupCryptography
{
    bool VerifyOwnerToken(string? ownerToken, string? expectedSha256);
    string GenerateSecret();
    string Protect(string secret);
    string Unprotect(string protectedSecret);
    long? FindMatchingStep(string secret, string? code, DateTimeOffset now, int allowedWindow);
    string GetAuthenticatorUri(string secret, string issuer, string userName);
}
