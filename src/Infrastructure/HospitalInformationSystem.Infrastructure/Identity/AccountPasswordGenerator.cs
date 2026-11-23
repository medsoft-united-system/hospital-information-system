using System.Security.Cryptography;
using HospitalInformationSystem.Application.Contracts.Abstractions;

namespace HospitalInformationSystem.Infrastructure.Identity;

public sealed class AccountPasswordGenerator : IAccountPasswordGenerator
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    public string Generate()
    {
        Span<char> password = stackalloc char[12];
        do
        {
            for (var index = 0; index < password.Length; index++)
                password[index] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        } while (!HasAllCharacterKinds(password));
        return new string(password);
    }

    private static bool HasAllCharacterKinds(ReadOnlySpan<char> password)
    {
        var hasUpper = false;
        var hasLower = false;
        var hasDigit = false;
        foreach (var character in password)
        {
            hasUpper |= char.IsAsciiLetterUpper(character);
            hasLower |= char.IsAsciiLetterLower(character);
            hasDigit |= char.IsAsciiDigit(character);
        }
        return hasUpper && hasLower && hasDigit;
    }
}
