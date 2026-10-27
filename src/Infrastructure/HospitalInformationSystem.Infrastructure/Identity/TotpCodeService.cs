using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace HospitalInformationSystem.Infrastructure.Identity;

public sealed class TotpCodeService
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public string GenerateSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(20);
        try
        {
            var output = new StringBuilder(32);
            var buffer = 0;
            var bits = 0;
            foreach (var value in bytes)
            {
                buffer = (buffer << 8) | value;
                bits += 8;
                while (bits >= 5)
                {
                    bits -= 5;
                    output.Append(Alphabet[(buffer >> bits) & 31]);
                }
            }
            return output.ToString();
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public string GetAuthenticatorUri(string secret, string issuer, string userName) =>
        $"otpauth://totp/{Uri.EscapeDataString($"{issuer}:{userName}")}?secret={Uri.EscapeDataString(secret)}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits=6&period=30";

    public string GetCode(string secret, DateTimeOffset at) => ComputeCode(secret, at.ToUnixTimeSeconds() / 30);

    public long? FindMatchingStep(string secret, string? code, DateTimeOffset now, int allowedWindow)
    {
        if (allowedWindow is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(allowedWindow));
        if (code is null || code.Length != 6 || code.Any(character => character is < '0' or > '9')) return null;
        var current = now.ToUnixTimeSeconds() / 30;
        var supplied = Encoding.ASCII.GetBytes(code);
        long? matched = null;
        foreach (var offset in allowedWindow == 0 ? new[] { 0 } : new[] { 0, -1, 1 })
        {
            var step = current + offset;
            if (step < 0) continue;
            var expected = Encoding.ASCII.GetBytes(ComputeCode(secret, step));
            var equal = CryptographicOperations.FixedTimeEquals(expected, supplied);
            if (equal && matched is null) matched = step;
        }
        return matched;
    }

    private static string ComputeCode(string secret, long step)
    {
        var key = DecodeSecret(secret);
        try
        {
            Span<byte> counter = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(counter, step);
            var digest = HMACSHA1.HashData(key, counter);
            var offset = digest[^1] & 15;
            var truncated = BinaryPrimitives.ReadUInt32BigEndian(digest.AsSpan(offset, 4)) & 0x7fffffff;
            return (truncated % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static byte[] DecodeSecret(string secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) throw new FormatException("Секрет TOTP не задан.");
        var output = new List<byte>();
        var buffer = 0;
        var bits = 0;
        foreach (var character in secret.TrimEnd('=').ToUpperInvariant())
        {
            var value = Alphabet.IndexOf(character);
            if (value < 0) throw new FormatException("Некорректный формат секрета TOTP.");
            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits < 8) continue;
            bits -= 8;
            output.Add((byte)(buffer >> bits));
        }
        if (output.Count < 20) throw new FormatException("Секрет TOTP должен содержать не менее 160 бит.");
        return output.ToArray();
    }
}
