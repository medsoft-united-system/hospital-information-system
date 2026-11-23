using System.Security.Cryptography;
using System.Text;
using HospitalInformationSystem.Application.Contracts.Abstractions;
using HospitalInformationSystem.Application.Models.ServiceAccounts;
using HospitalInformationSystem.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace HospitalInformationSystem.Infrastructure.ServiceAccounts;

public sealed class ServiceApiKeyGenerator : IServiceApiKeyGenerator
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Argon2idPasswordHasher _hasher;

    public ServiceApiKeyGenerator() : this(Options.Create(new Argon2idOptions())) { }

    public ServiceApiKeyGenerator(IOptions<Argon2idOptions> options) =>
        _hasher = new Argon2idPasswordHasher(options);

    public GeneratedServiceApiKey Generate(Guid serviceAccountId)
    {
        if (serviceAccountId == Guid.Empty) throw new ArgumentException("Укажите ID сервисного аккаунта.", nameof(serviceAccountId));
        var secret = RandomNumberGenerator.GetBytes(32);
        try
        {
            var apiKey = Prefix(serviceAccountId) + Encode(secret);
            return new GeneratedServiceApiKey(apiKey, Hash(apiKey));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    public byte[] Hash(string apiKey)
    {
        if (!TryGetAccountId(apiKey, out _))
            throw new ArgumentException("Некорректный формат API key.", nameof(apiKey));
        return Encoding.UTF8.GetBytes(_hasher.HashSecret(apiKey[37..]));
    }

    public static string Prefix(Guid serviceAccountId) => $"msa_{serviceAccountId:N}.";

    public static bool TryGetAccountId(string? apiKey, out Guid serviceAccountId)
    {
        serviceAccountId = Guid.Empty;
        if (apiKey is not { Length: 80 } || !apiKey.StartsWith("msa_", StringComparison.Ordinal) || apiKey[36] != '.' ||
            !Guid.TryParseExact(apiKey.AsSpan(4, 32), "N", out var id) || id == Guid.Empty ||
            apiKey.AsSpan(4, 32).ContainsAnyInRange('A', 'F') ||
            !TryDecode(apiKey.AsSpan(37), out var secret))
            return false;
        CryptographicOperations.ZeroMemory(secret);
        serviceAccountId = id;
        return true;
    }

    public ServiceApiKeyVerificationResult Verify(string? apiKey, Guid serviceAccountId, byte[] expectedHash)
    {
        if (!ServiceAccountRules.IsSupportedHash(expectedHash) ||
            !TryGetAccountId(apiKey, out var id) || id != serviceAccountId)
            return ServiceApiKeyVerificationResult.Failed;
        if (expectedHash.Length == ServiceAccountRules.LegacyHashLength)
        {
            if (!TryDecode(apiKey!.AsSpan(37), out var secret))
                return ServiceApiKeyVerificationResult.Failed;
            try
            {
                var actual = SHA256.HashData(secret);
                return CryptographicOperations.FixedTimeEquals(actual, expectedHash)
                    ? ServiceApiKeyVerificationResult.SuccessRehashNeeded
                    : ServiceApiKeyVerificationResult.Failed;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(secret);
            }
        }
        string encoded;
        try { encoded = StrictUtf8.GetString(expectedHash); }
        catch (DecoderFallbackException) { return ServiceApiKeyVerificationResult.Failed; }
        return _hasher.VerifyHashedSecret(encoded, apiKey![37..]) switch
        {
            PasswordVerificationResult.Success => ServiceApiKeyVerificationResult.Success,
            PasswordVerificationResult.SuccessRehashNeeded => ServiceApiKeyVerificationResult.SuccessRehashNeeded,
            _ => ServiceApiKeyVerificationResult.Failed
        };
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
