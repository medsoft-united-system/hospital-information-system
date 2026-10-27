using FluentAssertions;
using HospitalInformationSystem.Infrastructure.Identity;

namespace HospitalInformationSystem.UnitTests;

public sealed class TotpCodeServiceTests
{
    private const string Secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
    private readonly TotpCodeService _totp = new();

    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    [InlineData(20000000000L, "353130")]
    public void GetCode_Rfc6238Sha1Vectors_ShouldMatchSixDigitSuffix(long seconds, string expected)
    {
        var at = DateTimeOffset.FromUnixTimeSeconds(seconds);

        _totp.GetCode(Secret, at).Should().Be(expected);
        _totp.FindMatchingStep(Secret, expected, at, 0).Should().Be(seconds / 30);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("81804")]
    [InlineData("0818040")]
    [InlineData(" 081804")]
    [InlineData("０８１８０４")]
    [InlineData("abcdef")]
    public void FindMatchingStep_InvalidCodeShape_ShouldRequireExactlySixAsciiDigits(string? code)
    {
        _totp.FindMatchingStep(Secret, code, DateTimeOffset.FromUnixTimeSeconds(1111111109), 1).Should().BeNull();
    }

    [Fact]
    public void FindMatchingStep_AdjacentOrOldTimeStep_ShouldRespectConfiguredWindow()
    {
        var at = DateTimeOffset.FromUnixTimeSeconds(1234567890);
        var previous = _totp.GetCode(Secret, at.AddSeconds(-30));
        var old = _totp.GetCode(Secret, at.AddSeconds(-60));

        _totp.FindMatchingStep(Secret, previous, at, 1).Should().Be(at.ToUnixTimeSeconds() / 30 - 1);
        _totp.FindMatchingStep(Secret, previous, at, 0).Should().BeNull();
        _totp.FindMatchingStep(Secret, old, at, 1).Should().BeNull();
    }

    [Fact]
    public void GenerateSecret_NewAuthenticator_ShouldUseUniqueRandom160BitBase32Secrets()
    {
        var first = _totp.GenerateSecret();
        var second = _totp.GenerateSecret();

        first.Should().HaveLength(32).And.MatchRegex("^[A-Z2-7]{32}$");
        second.Should().NotBe(first);
        _totp.GetCode(first, DateTimeOffset.UtcNow).Should().HaveLength(6).And.MatchRegex("^[0-9]{6}$");
    }

    [Fact]
    public void GetAuthenticatorUri_ReservedLabelCharacters_ShouldEncodeIssuerAndAccountName()
    {
        var uri = _totp.GetAuthenticatorUri(Secret, "Med Soft &", "admin+test");

        uri.Should().StartWith("otpauth://totp/Med%20Soft%20%26%3Aadmin%2Btest?")
            .And.Contain("issuer=Med%20Soft%20%26").And.Contain("digits=6").And.Contain("period=30");
    }
}
