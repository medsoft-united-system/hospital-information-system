using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace HospitalInformationSystem.Infrastructure.Identity;

public sealed class Argon2idPasswordHasher : IPasswordHasher<HospitalUser>
{
    private const int SaltLength = 16;
    private const int HashLength = 32;
    private readonly PasswordHasher<HospitalUser> _legacyHasher = new();
    private readonly int _memorySize;
    private readonly int _iterations;
    private readonly int _parallelism;

    public Argon2idPasswordHasher() : this(Options.Create(new Argon2idOptions())) { }

    public Argon2idPasswordHasher(IOptions<Argon2idOptions> options)
    {
        var value = options.Value;
        var validation = new Argon2idOptionsValidator().Validate(Options.DefaultName, value);
        if (validation.Failed)
            throw new OptionsValidationException(Options.DefaultName, typeof(Argon2idOptions), validation.Failures);
        _memorySize = value.MemorySizeKiB;
        _iterations = value.Iterations;
        _parallelism = value.Parallelism;
    }

    public string HashPassword(HospitalUser user, string password) => HashSecret(password);

    public string HashSecret(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var hash = Derive(secret, salt, _memorySize, _iterations, _parallelism);
        try
        {
            return $"$argon2id$v=19$m={_memorySize},t={_iterations},p={_parallelism}${Encode(salt)}${Encode(hash)}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    public PasswordVerificationResult VerifyHashedPassword(HospitalUser user, string hashedPassword,
        string providedPassword)
    {
        if (string.IsNullOrEmpty(hashedPassword) || providedPassword is null || hashedPassword.Length > 512)
            return PasswordVerificationResult.Failed;
        if (!hashedPassword.StartsWith("$argon2id$", StringComparison.Ordinal))
            return VerifyLegacy(user, hashedPassword, providedPassword);
        return VerifyHashedSecret(hashedPassword, providedPassword);
    }

    public PasswordVerificationResult VerifyHashedSecret(string hashedSecret, string providedSecret)
    {
        if (string.IsNullOrEmpty(hashedSecret) || providedSecret is null || hashedSecret.Length > 512 ||
            !hashedSecret.StartsWith("$argon2id$", StringComparison.Ordinal))
            return PasswordVerificationResult.Failed;
        var parts = hashedSecret.Split('$');
        if (parts.Length != 6 || parts[2] != "v=19")
            return PasswordVerificationResult.Failed;
        var parameters = parts[3].Split(',');
        if (parameters.Length != 3 || !TryReadParameter(parameters[0], "m=", 19456, 262144, out var memory) ||
            !TryReadParameter(parameters[1], "t=", 2, 10, out var iterations) ||
            !TryReadParameter(parameters[2], "p=", 1, 4, out var parallelism))
            return PasswordVerificationResult.Failed;
        byte[] salt;
        byte[] expected;
        try
        {
            salt = Decode(parts[4]);
            expected = Decode(parts[5]);
        }
        catch (FormatException)
        {
            return PasswordVerificationResult.Failed;
        }
        if (salt.Length != SaltLength || expected.Length != HashLength)
            return PasswordVerificationResult.Failed;
        var actual = Derive(providedSecret, salt, memory, iterations, parallelism);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(expected, actual))
                return PasswordVerificationResult.Failed;
            return memory <= _memorySize && iterations <= _iterations &&
                   (memory < _memorySize || iterations < _iterations || parallelism != _parallelism)
                ? PasswordVerificationResult.SuccessRehashNeeded
                : PasswordVerificationResult.Success;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actual);
            CryptographicOperations.ZeroMemory(expected);
        }
    }

    private PasswordVerificationResult VerifyLegacy(HospitalUser user, string hash, string password)
    {
        try
        {
            var bytes = Convert.FromBase64String(hash);
            if (bytes.Length == 0 || bytes[0] is not (0 or 1))
                return PasswordVerificationResult.Failed;
            if (bytes[0] == 1 && (bytes.Length < 13 ||
                BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(5, 4)) > 1_000_000))
                return PasswordVerificationResult.Failed;
            return _legacyHasher.VerifyHashedPassword(user, hash, password) == PasswordVerificationResult.Failed
                ? PasswordVerificationResult.Failed
                : PasswordVerificationResult.SuccessRehashNeeded;
        }
        catch (FormatException)
        {
            return PasswordVerificationResult.Failed;
        }
    }

    private static byte[] Derive(string password, byte[] salt, int memory, int iterations, int parallelism)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            using var algorithm = new Argon2id(passwordBytes)
            {
                Salt = salt,
                MemorySize = memory,
                Iterations = iterations,
                DegreeOfParallelism = parallelism
            };
            return algorithm.GetBytes(HashLength);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    private static bool TryReadParameter(string value, string prefix, int minimum, int maximum, out int result)
    {
        result = 0;
        return value.StartsWith(prefix, StringComparison.Ordinal) &&
            int.TryParse(value.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out result) &&
            result >= minimum && result <= maximum;
    }

    private static string Encode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=');
    private static byte[] Decode(string value) => Convert.FromBase64String(value.PadRight((value.Length + 3) / 4 * 4, '='));
}
