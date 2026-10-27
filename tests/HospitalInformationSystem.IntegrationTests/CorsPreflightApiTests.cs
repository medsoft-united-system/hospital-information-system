using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class CorsPreflightApiTests(SecureHospitalApiFactory factory) : IClassFixture<SecureHospitalApiFactory>
{
    private const string AllowedOrigin = "https://localhost:5101";

    [Theory]
    [InlineData("/api/auth/setup/begin", "POST", "content-type,x-setup-token,x-csrf-token")]
    [InlineData("/api/patients", "POST", "authorization,content-type,x-csrf-token")]
    [InlineData("/api/medical-records/00000000-0000-0000-0000-000000000001/prescriptions/00000000-0000-0000-0000-000000000002", "PUT", "authorization,content-type,x-csrf-token")]
    public async Task Preflight_ConfiguredOriginAndSecurityHeaders_ShouldAllowCredentialedRequest(
        string path, string method, string headers)
    {
        using var host = factory.WithWebHostBuilder(builder => builder.UseSetting("Cors:AllowedOrigins:0", AllowedOrigin));
        using var client = host.CreateClient();
        using var request = Request(path, AllowedOrigin, method, headers);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        response.Headers.GetValues("Access-Control-Allow-Origin").Should().ContainSingle().Which.Should().Be(AllowedOrigin);
        response.Headers.GetValues("Access-Control-Allow-Credentials").Should().ContainSingle().Which.Should().Be("true");
        Values(response, "Access-Control-Allow-Methods").Should().Contain(method);
        Values(response, "Access-Control-Allow-Headers").Should()
            .Contain(headers.Split(','));
    }

    [Fact]
    public async Task Preflight_ForeignOrigin_ShouldNotGrantCorsPermission()
    {
        using var host = factory.WithWebHostBuilder(builder => builder.UseSetting("Cors:AllowedOrigins:0", AllowedOrigin));
        using var client = host.CreateClient();
        using var request = Request("/api/auth/setup/begin", "https://attacker.invalid", "POST",
            "authorization,x-setup-token,x-csrf-token");

        using var response = await client.SendAsync(request);

        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
        response.Headers.Contains("Access-Control-Allow-Credentials").Should().BeFalse();
    }

    [Fact]
    public async Task Preflight_UnconfiguredHeader_ShouldNotGrantHeaderPermission()
    {
        using var host = factory.WithWebHostBuilder(builder => builder.UseSetting("Cors:AllowedOrigins:0", AllowedOrigin));
        using var client = host.CreateClient();
        using var request = Request("/api/patients", AllowedOrigin, "POST", "authorization,x-unconfigured-header");

        using var response = await client.SendAsync(request);

        Values(response, "Access-Control-Allow-Headers").Should().NotContain("x-unconfigured-header");
    }

    private static HttpRequestMessage Request(string path, string origin, string method, string headers)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, path);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", method);
        request.Headers.Add("Access-Control-Request-Headers", headers);
        return request;
    }

    private static string[] Values(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values)
            ? values.SelectMany(value => value.Split(',', StringSplitOptions.TrimEntries))
                .Select(value => name.EndsWith("Headers", StringComparison.Ordinal) ? value.ToLowerInvariant() : value).ToArray()
            : [];
}
