using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace HospitalInformationSystem.Infrastructure.ServiceAccounts.Tokens;

public static class ServiceRefreshTokenMaterial
{
    public static string Generate() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

    public static string? Hash(string? token)
    {
        if (token is not { Length: 43 } ||
            token.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
            return null;
        byte[] secret;
        try { secret = Base64UrlEncoder.DecodeBytes(token); }
        catch (FormatException) { return null; }
        if (secret.Length != 32 || !string.Equals(token, Base64UrlEncoder.Encode(secret), StringComparison.Ordinal))
            return null;
        return Convert.ToHexStringLower(SHA256.HashData(secret));
    }
}
