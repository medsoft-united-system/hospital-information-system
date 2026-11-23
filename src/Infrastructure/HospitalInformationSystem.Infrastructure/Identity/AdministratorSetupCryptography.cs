using System.Security.Cryptography;
using System.Text;
using HospitalInformationSystem.Application.Contracts.Abstractions;
using Microsoft.AspNetCore.DataProtection;

namespace HospitalInformationSystem.Infrastructure.Identity;

public sealed class AdministratorSetupCryptography(IDataProtectionProvider protection, TotpCodeService totp)
    : IAdministratorSetupCryptography
{
    private readonly IDataProtector _protector = protection.CreateProtector("MedSoft.AdministratorTotp.v1");

    public bool VerifyOwnerToken(string? ownerToken, string? expectedSha256)
    {
        if (string.IsNullOrEmpty(ownerToken) || ownerToken.Length is < 32 or > 512 || expectedSha256?.Length != 64)
            return false;
        byte[] expected;
        try { expected = Convert.FromHexString(expectedSha256); }
        catch (FormatException) { return false; }
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(ownerToken));
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    public string GenerateSecret() => totp.GenerateSecret();
    public string Protect(string secret) => _protector.Protect(secret);
    public string Unprotect(string protectedSecret) => _protector.Unprotect(protectedSecret);
    public long? FindMatchingStep(string secret, string? code, DateTimeOffset now, int allowedWindow) =>
        totp.FindMatchingStep(secret, code, now, allowedWindow);
    public string GetAuthenticatorUri(string secret, string issuer, string userName) =>
        totp.GetAuthenticatorUri(secret, issuer, userName);
}
