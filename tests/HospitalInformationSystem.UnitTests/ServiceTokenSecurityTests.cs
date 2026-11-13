using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using FluentAssertions;
using HospitalInformationSystem.Contracts.ServiceAuth;
using HospitalInformationSystem.Infrastructure.Identity.Tokens;
using HospitalInformationSystem.Infrastructure.ServiceAccounts.Tokens;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace HospitalInformationSystem.UnitTests;

public sealed class ServiceTokenSecurityTests(JwtSigningKeyFixture fixture) : IClassFixture<JwtSigningKeyFixture>
{
    private static readonly DateTimeOffset Now = new(2030, 2, 3, 4, 5, 6, TimeSpan.Zero);

    [Fact]
    public void RefreshMaterial_GeneratedValues_ShouldBeIndependentCanonicalSecretsWithHashesOnly()
    {
        var values = Enumerable.Range(0, 128).Select(_ => ServiceRefreshTokenMaterial.Generate()).ToArray();

        values.Should().OnlyHaveUniqueItems().And.OnlyContain(value => value.Length == 43);
        foreach (var value in values)
        {
            var bytes = Base64UrlEncoder.DecodeBytes(value);
            bytes.Should().HaveCount(32);
            Base64UrlEncoder.Encode(bytes).Should().Be(value);
            ServiceRefreshTokenMaterial.Hash(value).Should().Be(Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("opaque-but-short")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA/")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAC")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAD")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA_")]
    public void RefreshMaterial_InvalidOrNonCanonicalSecret_ShouldHaveNoHash(string? value) =>
        ServiceRefreshTokenMaterial.Hash(value).Should().BeNull();

    [Theory]
    [InlineData(0, 7)]
    [InlineData(16, 7)]
    [InlineData(5, 0)]
    [InlineData(5, 31)]
    public void Options_UnsafeLifetimes_ShouldFailValidation(int accessMinutes, int refreshDays)
    {
        var options = new ServiceTokenOptions { AccessTokenMinutes = accessMinutes, RefreshTokenDays = refreshDays };

        Validator().Validate(null, options).Failed.Should().BeTrue();
    }

    [Theory]
    [InlineData("", "MedSoft.Lis")]
    [InlineData(" MedSoft.Api", "MedSoft.Lis")]
    [InlineData("MedSoft.Api", "MedSoft.Lis ")]
    [InlineData("MedSoft.Api", "MedSoft.Web")]
    [InlineData("MedSoft.Api\n", "MedSoft.Lis")]
    public void Options_InvalidIdentifiersOrHumanAudience_ShouldFailValidation(string issuer, string audience) =>
        Validator().Validate(null, new ServiceTokenOptions { Issuer = issuer, Audience = audience }).Failed.Should().BeTrue();

    [Fact]
    public void Options_Defaults_ShouldBeDistinctFromHumanAuthentication() =>
        Validator().Validate(null, new ServiceTokenOptions()).Succeeded.Should().BeTrue();

    [Fact]
    public void ValidationParameters_ValidRs256_ShouldPreserveServiceIdentityAndHumanParameters()
    {
        var provider = Parameters();
        var token = Token(Now.AddMinutes(-1), Now.AddMinutes(4));

        var principal = Handler().ValidateToken(token, provider.ValidationParameters, out var validated);

        principal.Identity!.AuthenticationType.Should().Be(ServiceAuthenticationDefaults.Scheme);
        principal.IsInRole(ServiceAuthenticationDefaults.Role).Should().BeTrue();
        validated.Should().BeOfType<JwtSecurityToken>().Which.Header.Alg.Should().Be(SecurityAlgorithms.RsaSha256);
        fixture.Keys.ValidationParameters.ValidAudience.Should().Be("MedSoft.Web");
        provider.ValidationParameters.Should().NotBeSameAs(provider.ValidationParameters);
        var changed = provider.ValidationParameters;
        changed.ValidAudience = "modified";
        provider.ValidationParameters.ValidAudience.Should().Be(ServiceAuthenticationDefaults.Audience);
    }

    [Theory]
    [InlineData("MedSoft.Api", "MedSoft.Web")]
    [InlineData("wrong-issuer", "MedSoft.Lis")]
    [InlineData("MedSoft.Api", "wrong-audience")]
    public void ValidationParameters_ForeignIssuerOrAudience_ShouldReject(string issuer, string audience)
    {
        var token = Token(Now.AddMinutes(-1), Now.AddMinutes(4), issuer, audience);

        Action validate = () => Handler().ValidateToken(token, Parameters().ValidationParameters, out _);

        validate.Should().Throw<SecurityTokenException>();
    }

    [Fact]
    public void ValidationParameters_MixedHumanAndServiceAudiences_ShouldReject()
    {
        var payload = new JwtPayload(ServiceAuthenticationDefaults.Issuer, null, Claims(),
            Now.AddMinutes(-1).UtcDateTime, Now.AddMinutes(4).UtcDateTime)
        { [JwtRegisteredClaimNames.Aud] = new[] { ServiceAuthenticationDefaults.Audience, "MedSoft.Web" } };
        var token = Handler().WriteToken(new JwtSecurityToken(new JwtHeader(fixture.Keys.SigningCredentials), payload));

        Action validate = () => Handler().ValidateToken(token, Parameters().ValidationParameters, out _);

        validate.Should().Throw<SecurityTokenException>();
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 60)]
    [InlineData(-60, -1)]
    public void ValidationParameters_ExpiredOrNotYetActive_ShouldRejectWithoutClockSkew(int nbfDelta, int expDelta)
    {
        var payload = new JwtPayload
        {
            [JwtRegisteredClaimNames.Iss] = ServiceAuthenticationDefaults.Issuer,
            [JwtRegisteredClaimNames.Aud] = ServiceAuthenticationDefaults.Audience,
            [JwtRegisteredClaimNames.Nbf] = Now.AddSeconds(nbfDelta).ToUnixTimeSeconds(),
            [JwtRegisteredClaimNames.Exp] = Now.AddSeconds(expDelta).ToUnixTimeSeconds()
        };
        var token = Handler().WriteToken(new JwtSecurityToken(new JwtHeader(fixture.Keys.SigningCredentials), payload));

        Action validate = () => Handler().ValidateToken(token, Parameters().ValidationParameters, out _);

        validate.Should().Throw<SecurityTokenException>();
    }

    [Fact]
    public void ValidationParameters_MissingNotBefore_ShouldReject()
    {
        var payload = new JwtPayload
        {
            [JwtRegisteredClaimNames.Iss] = ServiceAuthenticationDefaults.Issuer,
            [JwtRegisteredClaimNames.Aud] = ServiceAuthenticationDefaults.Audience,
            [JwtRegisteredClaimNames.Exp] = Now.AddMinutes(1).ToUnixTimeSeconds()
        };
        var token = Handler().WriteToken(new JwtSecurityToken(new JwtHeader(fixture.Keys.SigningCredentials), payload));

        Action validate = () => Handler().ValidateToken(token, Parameters().ValidationParameters, out _);

        validate.Should().Throw<SecurityTokenException>();
    }

    [Fact]
    public void ValidationParameters_UnknownKidOrSymmetricAlgorithm_ShouldReject()
    {
        var unknown = new SigningCredentials(new RsaSecurityKey(((RsaSecurityKey)fixture.Keys.SigningCredentials.Key).Rsa)
            { KeyId = "unconfigured-service-key" }, SecurityAlgorithms.RsaSha256);
        var symmetric = new SigningCredentials(new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(64))
            { KeyId = fixture.Options.KeyId }, SecurityAlgorithms.HmacSha256);
        foreach (var signing in new[] { unknown, symmetric })
        {
            var token = Token(Now.AddMinutes(-1), Now.AddMinutes(4), credentials: signing);
            Action validate = () => Handler().ValidateToken(token, Parameters().ValidationParameters, out _);
            validate.Should().Throw<SecurityTokenException>();
        }
    }

    [Theory]
    [InlineData("sub")]
    [InlineData("sid")]
    [InlineData("jti")]
    [InlineData("auth_ver")]
    [InlineData("role")]
    [InlineData("account_kind")]
    [InlineData("iat")]
    [InlineData("nbf")]
    [InlineData("exp")]
    public async Task ValidatePrincipal_MissingOrDuplicatedSecurityClaim_ShouldRejectBeforeDatabase(string type)
    {
        var claims = Claims().Where(claim => claim.Type != type).ToArray();
        var service = SessionService();

        (await service.ValidatePrincipalAsync(Principal(claims), CancellationToken.None)).Should().BeFalse();
        var duplicated = Claims().Concat([Claims().Single(claim => claim.Type == type)]);
        (await service.ValidatePrincipalAsync(Principal(duplicated), CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task ValidatePrincipal_HumanOrMixedIdentity_ShouldRejectBeforeDatabase()
    {
        var service = SessionService();
        var human = new ClaimsPrincipal(new ClaimsIdentity(Claims(), "Bearer"));
        (await service.ValidatePrincipalAsync(human, CancellationToken.None)).Should().BeFalse();
        var mixed = Principal(Claims());
        mixed.AddIdentity(new ClaimsIdentity([new Claim("role", "Administrator")], "Bearer"));
        (await service.ValidatePrincipalAsync(mixed, CancellationToken.None)).Should().BeFalse();
    }

    private ServiceTokenSessionService SessionService() => new(null!, fixture.Keys,
        Options.Create(new ServiceTokenOptions()), new FixedClock(), null!, null!, null!);

    private ServiceTokenValidationParameters Parameters() => new(fixture.Keys,
        Options.Create(new ServiceTokenOptions()), new FixedClock());

    private ServiceTokenOptionsValidator Validator() => new(Options.Create(fixture.Options));
    private static JwtSecurityTokenHandler Handler() => new() { MapInboundClaims = false };
    private static ClaimsPrincipal Principal(IEnumerable<Claim> claims) => new(new ClaimsIdentity(claims,
        ServiceAuthenticationDefaults.Scheme, "name", ServiceAuthenticationDefaults.Claims.Role));

    private string Token(DateTimeOffset notBefore, DateTimeOffset expires,
        string issuer = ServiceAuthenticationDefaults.Issuer, string audience = ServiceAuthenticationDefaults.Audience,
        SigningCredentials? credentials = null) => Handler().WriteToken(new JwtSecurityToken(issuer, audience,
        Claims(), notBefore.UtcDateTime, expires.UtcDateTime, credentials ?? fixture.Keys.SigningCredentials));

    private static Claim[] Claims() =>
    [
        new("sub", "11111111-1111-4111-8111-111111111111"),
        new("sid", "22222222-2222-4222-8222-222222222222"),
        new("jti", "33333333-3333-4333-8333-333333333333"),
        new("account_kind", "service"), new("role", "LIS"), new("auth_ver", "1"),
        new("iat", Now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64),
        new("nbf", Now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64),
        new("exp", Now.AddMinutes(5).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64)
    ];

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
