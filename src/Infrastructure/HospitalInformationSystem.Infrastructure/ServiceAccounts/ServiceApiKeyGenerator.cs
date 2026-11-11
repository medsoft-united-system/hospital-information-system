using System.Security.Cryptography;
using HospitalInformationSystem.Application.Abstractions;
using HospitalInformationSystem.Application.ServiceAccounts;

namespace HospitalInformationSystem.Infrastructure.ServiceAccounts;

public sealed class ServiceApiKeyGenerator : IServiceApiKeyGenerator
{
    public GeneratedServiceApiKey Generate(Guid serviceAccountId)
    {
        if (serviceAccountId == Guid.Empty) throw new ArgumentException("Укажите ID сервисного аккаунта.", nameof(serviceAccountId));
        var secret = RandomNumberGenerator.GetBytes(32);
        return new GeneratedServiceApiKey(Prefix(serviceAccountId) + Encode(secret), SHA256.HashData(secret));
    }

    public static string Prefix(Guid serviceAccountId) => $"msa_{serviceAccountId:N}.";

    public static bool TryGetAccountId(string? apiKey, out Guid serviceAccountId)
    {
        serviceAccountId = Guid.Empty;
        if (apiKey is not { Length: 80 } || !apiKey.StartsWith("msa_", StringComparison.Ordinal) || apiKey[36] != '.' ||
            !Guid.TryParseExact(apiKey.AsSpan(4, 32), "N", out var id) || id == Guid.Empty ||
            apiKey.AsSpan(4, 32).ContainsAnyInRange('A', 'F') ||
            !TryDecode(apiKey.AsSpan(37), out _))
            return false;
        serviceAccountId = id;
        return true;
    }

    public static bool Verify(string? apiKey, Guid serviceAccountId, ReadOnlySpan<byte> expectedHash)
    {
        if (expectedHash.Length != 32 || !TryGetAccountId(apiKey, out var id) || id != serviceAccountId ||
            !TryDecode(apiKey!.AsSpan(37), out var secret))
            return false;
        var hash = SHA256.HashData(secret);
        return CryptographicOperations.FixedTimeEquals(hash, expectedHash);
    }

    private static string Encode(ReadOnlySpan<byte> secret) =>
        Convert.ToBase64String(secret).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryDecode(ReadOnlySpan<char> text, out byte[] secret)
    {
        secret = [];
        if (text.Length != 43) return false;
        foreach (var character in text)
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')) return false;
        Span<byte> bytes = stackalloc byte[32];
        var base64 = text.ToString().Replace('-', '+').Replace('_', '/') + "=";
        if (!Convert.TryFromBase64String(base64, bytes, out var written) || written != 32 ||
            !text.SequenceEqual(Encode(bytes).AsSpan()))
            return false;
        secret = bytes.ToArray();
        return true;
    }
}
