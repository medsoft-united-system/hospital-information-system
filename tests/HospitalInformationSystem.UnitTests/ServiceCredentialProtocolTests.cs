using System.Text;
using System.Text.Json;
using FluentAssertions;
using HospitalInformationSystem.Api.Security;
using HospitalInformationSystem.Application.Models.ServiceAuth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace HospitalInformationSystem.UnitTests;

public sealed class ServiceCredentialProtocolTests
{
    [Theory]
    [InlineData("application/json")]
    [InlineData("Application/Json; charset=UTF-8")]
    [InlineData("application/json; charset=\"utf-8\"")]
    public async Task Reader_AcceptsExactCredentialWithoutNormalizingSecret(string contentType)
    {
        var request = Request("""{"apiKey":"  unchanged-credential  "}""", contentType);

        (await new ServiceCredentialReader().ReadAsync(request, "apiKey", CancellationToken.None))
            .Should().Be("  unchanged-credential  ");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("""{"apiKey":null}""")]
    [InlineData("""{"apiKey":3}""")]
    [InlineData("""{"apiKey":""}""")]
    [InlineData("""{"apiKey":"  "}""")]
    [InlineData("""{"ApiKey":"value"}""")]
    [InlineData("""{"apiKey":"value","ApiKey":"other"}""")]
    [InlineData("""{"apiKey":"value","apiKey":"other"}""")]
    [InlineData("""{"apiKey":"value","role":"LIS"}""")]
    [InlineData("""{"apiKey":"bad\u0000credential"}""")]
    [InlineData("""{"apiKey":"bad\ud800credential"}""")]
    [InlineData("""{"apiKey":{"nested":"value"}}""")]
    [InlineData("""{"apiKey":"value",}""")]
    public async Task Reader_RejectsAmbiguousOrMalformedCredentials(string body)
    {
        var read = () => new ServiceCredentialReader().ReadAsync(Request(body), "apiKey", CancellationToken.None);

        (await read.Should().ThrowAsync<ServiceAuthHttpException>()).Which.Code.Should()
            .Be(ServiceAuthErrorCodes.InvalidRequest);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("text/json")]
    [InlineData("application/fhir+json")]
    [InlineData("application/json; charset=utf-16")]
    [InlineData("application/json; charset=utf-8; charset=utf-8")]
    [InlineData("application/json; unknown=value")]
    public async Task Reader_RejectsUnsupportedMediaType(string? contentType)
    {
        var read = () => new ServiceCredentialReader().ReadAsync(Request("{}", contentType), "apiKey", CancellationToken.None);

        (await read.Should().ThrowAsync<ServiceAuthHttpException>()).Which.StatusCode.Should().Be(415);
    }

    [Fact]
    public async Task Reader_RejectsInvalidUtf8AndCredentialsInQuery()
    {
        var invalidUtf8 = Request("{}");
        invalidUtf8.Body = new MemoryStream([0xff, 0xfe]);
        var read = () => new ServiceCredentialReader().ReadAsync(invalidUtf8, "apiKey", CancellationToken.None);
        (await read.Should().ThrowAsync<ServiceAuthHttpException>()).Which.StatusCode.Should().Be(400);
        var query = Request("""{"apiKey":"value"}""");
        query.QueryString = new QueryString("?apiKey=value");
        read = () => new ServiceCredentialReader().ReadAsync(query, "apiKey", CancellationToken.None);
        (await read.Should().ThrowAsync<ServiceAuthHttpException>()).Which.StatusCode.Should().Be(400);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reader_BoundsBodiesWithAndWithoutContentLength(bool knownLength)
    {
        var request = Request(new string(' ', ServiceAuthProtocol.MaxBodyBytes + 1));
        if (knownLength) request.ContentLength = ServiceAuthProtocol.MaxBodyBytes + 1;
        var read = () => new ServiceCredentialReader().ReadAsync(request, "apiKey", CancellationToken.None);

        (await read.Should().ThrowAsync<ServiceAuthHttpException>()).Which.StatusCode.Should().Be(413);
    }

    [Theory]
    [InlineData("POST", "/api/service-auth/token", true, true)]
    [InlineData("POST", "/API/SERVICE-AUTH/REFRESH/", true, true)]
    [InlineData("POST", "/api/service-auth/token", false, false)]
    [InlineData("PUT", "/api/service-auth/token", true, false)]
    [InlineData("POST", "/api/service-auth/token/extra", true, false)]
    [InlineData("POST", "/api/service-auth/unknown", true, false)]
    [InlineData("POST", "/api/accounts", true, false)]
    public void CsrfExemption_RequiresKnownPostAndEndpointMarker(string method, string path, bool marked, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        if (marked) context.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
            new EndpointMetadataCollection(new ServiceCredentialEndpointAttribute()), "credential"));

        ServiceAuthProtocol.IsCredentialRequest(context).Should().Be(expected);
    }

    [Fact]
    public async Task Protocol_ConvertsUnexpectedExceptionsWithoutExposingTheirBodyOrMessage()
    {
        var context = new DefaultHttpContext();
        context.TraceIdentifier = "safe-trace";
        context.Request.Path = "/api/service-auth/token";
        context.Response.Body = new MemoryStream();
        var middleware = new ServiceAuthProtocolMiddleware(async c =>
        {
            await c.Response.WriteAsync("plaintext-secret-in-partial-output");
            throw new InvalidOperationException("plaintext-secret-in-exception");
        }, NullLogger<ServiceAuthProtocolMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(500);
        context.Response.Headers.CacheControl.ToString().Should().Be("no-store");
        context.Response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(context.Response.Body);
        json.RootElement.GetProperty("code").GetString().Should().Be(ServiceAuthErrorCodes.InternalError);
        json.RootElement.GetProperty("traceId").GetString().Should().Be("safe-trace");
        json.RootElement.GetRawText().Should().NotContain("plaintext-secret");
    }

    private static HttpRequest Request(string body, string? contentType = "application/json")
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = contentType;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return context.Request;
    }
}
