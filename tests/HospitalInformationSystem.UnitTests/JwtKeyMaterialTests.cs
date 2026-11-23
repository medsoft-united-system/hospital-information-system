using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using FluentAssertions;
using HospitalInformationSystem.Application.Models.Auth;
using HospitalInformationSystem.Infrastructure.Identity.Tokens;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace HospitalInformationSystem.UnitTests;

public sealed class JwtKeyMaterialTests(JwtSigningKeyFixture fixture) : IClassFixture<JwtSigningKeyFixture>
{
    [Fact]
    public void ValidateToken_ValidRs256Token_ShouldVerifySignatureAndExplicitRoleClaim()
    {
        var token = CreateToken(fixture.Options.Issuer, fixture.Options.Audience, DateTime.UtcNow.AddMinutes(1),
            fixture.Keys.SigningCredentials);

        var principal = Handler().ValidateToken(token, fixture.Keys.ValidationParameters, out var validated);

        validated.Should().BeOfType<JwtSecurityToken>().Which.Header.Alg.Should().Be(SecurityAlgorithms.RsaSha256);
        principal.IsInRole(ApplicationRoles.MedicalWorker).Should().BeTrue();
        principal.Identity!.Name.Should().Be("doctor.test");
    }

    [Theory]
    [InlineData("wrong-issuer", "MedSoft.Web", false)]
    [InlineData("MedSoft.Api", "wrong-audience", false)]
    [InlineData("MedSoft.Api", "MedSoft.Web", true)]
    public void ValidateToken_WrongIssuerAudienceOrExpiredToken_ShouldReject(string issuer, string audience, bool expired)
    {
        var token = CreateToken(issuer, audience, expired ? DateTime.UtcNow.AddMinutes(-1) : DateTime.UtcNow.AddMinutes(1),
            fixture.Keys.SigningCredentials);

        Action validate = () => Handler().ValidateToken(token, fixture.Keys.ValidationParameters, out _);

        validate.Should().Throw<SecurityTokenException>();
    }

    [Fact]
    public void ValidateToken_TamperedPayload_ShouldRejectSignature()
    {
        var token = CreateToken(fixture.Options.Issuer, fixture.Options.Audience, DateTime.UtcNow.AddMinutes(1),
            fixture.Keys.SigningCredentials);
        var parts = token.Split('.');
        var payload = Base64UrlEncoder.Decode(parts[1]).Replace("doctor.test", "hacker.test", StringComparison.Ordinal);
        parts[1] = Base64UrlEncoder.Encode(payload);

        Action validate = () => Handler().ValidateToken(string.Join('.', parts), fixture.Keys.ValidationParameters, out _);

        validate.Should().Throw<SecurityTokenException>();
    }

    [Fact]
    public void ValidateToken_HmacAlgorithmConfusion_ShouldRejectSymmetricSignature()
    {
        var symmetricKey = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(64)) { KeyId = fixture.Options.KeyId };
        var token = CreateToken(fixture.Options.Issuer, fixture.Options.Audience, DateTime.UtcNow.AddMinutes(1),
            new SigningCredentials(symmetricKey, SecurityAlgorithms.HmacSha256));

        Action validate = () => Handler().ValidateToken(token, fixture.Keys.ValidationParameters, out _);

        validate.Should().Throw<SecurityTokenException>();
    }

    [Fact]
    public void ValidateToken_UnknownKid_ShouldRejectBeforeTryingUnrelatedKeys()
    {
        var credentials = new SigningCredentials(new RsaSecurityKey(((RsaSecurityKey)fixture.Keys.SigningCredentials.Key).Rsa)
        { KeyId = "unknown-key" }, SecurityAlgorithms.RsaSha256);
        var token = CreateToken(fixture.Options.Issuer, fixture.Options.Audience, DateTime.UtcNow.AddMinutes(1), credentials);

        Action validate = () => Handler().ValidateToken(token, fixture.Keys.ValidationParameters, out _);

        validate.Should().Throw<SecurityTokenException>();
    }

    [Fact]
    public void ValidateToken_FutureNotBeforeWithinThirtySeconds_ShouldRejectBeforeActivation()
    {
        var now = DateTimeOffset.UtcNow;
        var token = CreateRawTimeToken(now.AddSeconds(10).ToUnixTimeSeconds(), now.AddMinutes(2).ToUnixTimeSeconds());

        Action validate = () => Handler().ValidateToken(token, fixture.Keys.ValidationParameters, out _);

        validate.Should().Throw<SecurityTokenException>();
    }

    [Fact]
    public void ValidateToken_NotBeforeAfterExpiration_ShouldRejectInvalidLifetimeInterval()
    {
        var now = DateTimeOffset.UtcNow;
        var token = CreateRawTimeToken(now.AddSeconds(20).ToUnixTimeSeconds(), now.AddSeconds(10).ToUnixTimeSeconds());

        Action validate = () => Handler().ValidateToken(token, fixture.Keys.ValidationParameters, out _);

        validate.Should().Throw<SecurityTokenException>();
    }

    [Fact]
    public void ValidateToken_MissingExpiration_ShouldRequireFiniteLifetime()
    {
        var token = CreateRawTimeToken(DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds(), null);

        Action validate = () => Handler().ValidateToken(token, fixture.Keys.ValidationParameters, out _);

        validate.Should().Throw<SecurityTokenException>();
    }

    private string CreateRawTimeToken(long notBefore, long? expires)
    {
        var payload = new JwtPayload
        {
            [JwtRegisteredClaimNames.Iss] = fixture.Options.Issuer,
            [JwtRegisteredClaimNames.Aud] = fixture.Options.Audience,
            [JwtRegisteredClaimNames.Nbf] = notBefore
        };
        if (expires is { } expiration) payload[JwtRegisteredClaimNames.Exp] = expiration;
        return Handler().WriteToken(new JwtSecurityToken(new JwtHeader(fixture.Keys.SigningCredentials), payload));
    }

    [Fact]
    public void CreateKeyMaterial_MissingExternalKeyWithoutExplicitTestMode_ShouldReject()
    {
        Action create = () => new JwtKeyMaterial(Options.Create(new JwtTokenOptions()), TimeProvider.System);

        create.Should().Throw<InvalidOperationException>();
    }

    private static JwtSecurityTokenHandler Handler() => new() { MapInboundClaims = false };

    private static string CreateToken(string issuer, string audience, DateTime expires, SigningCredentials credentials)
    {
        var token = new JwtSecurityToken(issuer, audience,
            [new Claim(SecurityTokenClaimTypes.AccountRole, ApplicationRoles.MedicalWorker),
                new Claim(SecurityTokenClaimTypes.UserName, "doctor.test")],
            DateTime.UtcNow.AddMinutes(-2), expires, credentials);
        return Handler().WriteToken(token);
    }
}
