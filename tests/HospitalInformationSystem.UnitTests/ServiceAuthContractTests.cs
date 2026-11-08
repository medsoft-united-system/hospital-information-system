using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using HospitalInformationSystem.Contracts.Auth;
using HospitalInformationSystem.Contracts.ServiceAuth;

namespace HospitalInformationSystem.UnitTests;

public sealed class ServiceAuthContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false
    };

    [Fact]
    public void TokenRequestUsesOnlyOpaqueApiKey()
    {
        var request = Read<ServiceTokenRequest>("service-token-request.json");

        request.ApiKey.Should().Be("fixture-api-key-not-a-secret");
        AssertFields(request, "apiKey");
        Read<ServiceTokenRequest>(Serialize(request)).Should().Be(request);
    }

    [Fact]
    public void RefreshRequestUsesOnlyOpaqueRefreshToken()
    {
        var request = Read<ServiceRefreshRequest>("service-refresh-request.json");

        request.RefreshToken.Should().Be("fixture-refresh-token-not-a-secret");
        AssertFields(request, "refreshToken");
        Read<ServiceRefreshRequest>(Serialize(request)).Should().Be(request);
    }

    [Fact]
    public void TokenPairExposesAbsoluteExpiryAndNoHumanSessionFields()
    {
        var pair = Read<ServiceTokenPairDto>("service-token-response.json");

        pair.AccessToken.Should().Be("fixture-access-token-not-a-jwt");
        pair.RefreshToken.Should().Be("fixture-refresh-token-not-a-secret");
        pair.AccessTokenExpiresAt.Should().Be(new DateTimeOffset(2026, 11, 5, 10, 10, 0, TimeSpan.Zero));
        pair.RefreshTokenExpiresAt.Should().Be(new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero));
        pair.AccessTokenExpiresAt.Offset.Should().Be(TimeSpan.Zero);
        pair.RefreshTokenExpiresAt.Should().BeAfter(pair.AccessTokenExpiresAt);
        AssertFields(pair, "accessToken", "accessTokenExpiresAt", "refreshToken", "refreshTokenExpiresAt");
        Read<ServiceTokenPairDto>(Serialize(pair)).Should().Be(pair);
    }

    [Fact]
    public void ConnectionProbeIdentifiesTheServiceWithoutCredentials()
    {
        var status = Read<ServiceConnectionStatusDto>("test-connection.json");

        status.Status.Should().Be("connected");
        status.ServiceAccountId.Should().Be(Guid.Parse("55555555-5555-4555-8555-555555555555"));
        status.Role.Should().Be(ServiceAuthenticationDefaults.Role);
        status.FhirVersion.Should().Be(ServiceAuthenticationDefaults.FhirVersion);
        AssertFields(status, "status", "serviceAccountId", "role", "fhirVersion");
        Read<ServiceConnectionStatusDto>(Serialize(status)).Should().Be(status);
    }

    [Fact]
    public void AuthenticationErrorUsesTechnicalJsonInsteadOfFhirOrProblemDetails()
    {
        var error = Read<ServiceAuthErrorDto>("service-auth-error.json");

        error.Code.Should().Be(ServiceAuthErrorCodes.InvalidCredentials);
        error.Message.Should().NotBeNullOrWhiteSpace();
        error.TraceId.Should().NotBeNullOrWhiteSpace();
        AssertFields(error, "code", "message", "traceId");
        Read<ServiceAuthErrorDto>(Serialize(error)).Should().Be(error);
    }

    [Theory]
    [InlineData(typeof(ServiceTokenRequest), "service-token-request.json")]
    [InlineData(typeof(ServiceRefreshRequest), "service-refresh-request.json")]
    [InlineData(typeof(ServiceTokenPairDto), "service-token-response.json")]
    [InlineData(typeof(ServiceConnectionStatusDto), "test-connection.json")]
    [InlineData(typeof(ServiceAuthErrorDto), "service-auth-error.json")]
    public void MissingMandatoryWireFieldsAreRejected(Type type, string fixture)
    {
        var original = JsonNode.Parse(Fixture(fixture))!.AsObject();
        foreach (var field in original.Select(property => property.Key).ToArray())
        {
            var missingField = original.DeepClone().AsObject();
            missingField.Remove(field);

            var deserialize = () => JsonSerializer.Deserialize(missingField.ToJsonString(), type, JsonOptions);

            deserialize.Should().Throw<JsonException>($"{field} is required by C2");
        }
    }

    [Fact]
    public void ServiceClaimsStayDistinctFromHumanSecurityClaims()
    {
        ServiceAuthenticationDefaults.Claims.AccountKind.Should().Be("account_kind");
        ServiceAuthenticationDefaults.Claims.AuthenticationVersion.Should().Be("auth_ver");
        ServiceAuthenticationDefaults.Claims.AuthenticationVersion.Should().NotBe(SecurityTokenClaimTypes.AuthenticationVersion);
        ServiceAuthenticationDefaults.Claims.Role.Should().NotBe(SecurityTokenClaimTypes.AccountRole);
        ServiceAuthenticationDefaults.Claims.SessionId.Should().Be("sid");
        ServiceAuthenticationDefaults.Claims.TokenId.Should().Be("jti");
    }

    [Theory]
    [InlineData("2026-11-05T13:10:00+03:00", "2026-11-05T10:10:00.000Z")]
    [InlineData("2026-11-05T10:10:00Z", "2026-11-05T10:10:00.000Z")]
    [InlineData("2026-11-05T10:10:00.1Z", "2026-11-05T10:10:00.100Z")]
    [InlineData("2026-11-05T10:10:00.123Z", "2026-11-05T10:10:00.123Z")]
    public void ExpiryNormalizesTimezoneAndPrecision(string timestamp, string expected)
    {
        var original = JsonNode.Parse(Fixture("service-token-response.json"))!.AsObject();
        original["accessTokenExpiresAt"] = timestamp;

        var pair = Read<ServiceTokenPairDto>(original.ToJsonString());
        var serialized = JsonSerializer.SerializeToElement(pair, JsonOptions);

        pair.AccessTokenExpiresAt.Offset.Should().Be(TimeSpan.Zero);
        serialized.GetProperty("accessTokenExpiresAt").GetString().Should().Be(expected);
    }

    [Fact]
    public void OutgoingExpiryTruncatesPrecisionBeyondMilliseconds()
    {
        var expiry = new DateTimeOffset(2026, 11, 5, 13, 10, 0, TimeSpan.FromHours(3)).AddTicks(1_234_567);
        var pair = new ServiceTokenPairDto("fixture-access", expiry, "fixture-refresh", expiry.AddDays(7));

        var serialized = JsonSerializer.SerializeToElement(pair, JsonOptions);

        serialized.GetProperty("accessTokenExpiresAt").GetString().Should().Be("2026-11-05T10:10:00.123Z");
        serialized.GetProperty("refreshTokenExpiresAt").GetString().Should().Be("2026-11-12T10:10:00.123Z");
    }

    [Theory]
    [InlineData("2026-11-05T10:10:00")]
    [InlineData("2026-11-05")]
    [InlineData("2026-02-30T10:10:00Z")]
    [InlineData("2026-11-05T10:10:00.1234Z")]
    [InlineData("2026-11-05T10:10:00+0300")]
    [InlineData("2026-11-05T10:10:00+99:99")]
    public void InvalidOrUnsupportedIncomingExpiryIsRejected(string timestamp)
    {
        var original = JsonNode.Parse(Fixture("service-token-response.json"))!.AsObject();
        original["accessTokenExpiresAt"] = timestamp;

        var deserialize = () => Read<ServiceTokenPairDto>(original.ToJsonString());

        deserialize.Should().Throw<JsonException>();
    }

    private static T Read<T>(string fixtureOrJson) => JsonSerializer.Deserialize<T>(
        fixtureOrJson.StartsWith('{') ? fixtureOrJson : Fixture(fixtureOrJson), JsonOptions)!;

    private static string Fixture(string name) => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Lab3", name));

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    private static void AssertFields<T>(T value, params string[] fields) => JsonSerializer
        .SerializeToElement(value, JsonOptions).EnumerateObject().Select(property => property.Name)
        .Should().BeEquivalentTo(fields);
}
