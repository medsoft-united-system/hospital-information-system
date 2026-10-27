using FluentAssertions;
using HospitalInformationSystem.Infrastructure.Identity;

namespace HospitalInformationSystem.UnitTests;

public sealed class AccountPasswordGeneratorTests
{
    [Fact]
    public void Generate_MultiplePasswords_ShouldReturnDistinctTwelveCharacterAlphanumericSecrets()
    {
        var generator = new AccountPasswordGenerator();
        var passwords = Enumerable.Range(0, 100).Select(_ => generator.Generate()).ToArray();

        passwords.Should().OnlyHaveUniqueItems();
        foreach (var password in passwords)
        {
            password.Should().HaveLength(12);
            password.Should().MatchRegex("^[A-Za-z0-9]{12}$");
            password.Should().ContainAny("A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M", "N",
                "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z");
            password.Any(char.IsAsciiLetterLower).Should().BeTrue();
            password.Any(char.IsAsciiDigit).Should().BeTrue();
        }
    }
}
