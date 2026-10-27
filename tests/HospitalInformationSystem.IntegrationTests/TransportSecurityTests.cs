using FluentAssertions;
using HospitalInformationSystem.Api.Middleware;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class TransportSecurityTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    [InlineData("Production")]
    public async Task Invoke_PlaintextRequest_ShouldRejectBeforeCredentialsReachEndpoints(string environment)
    {
        using var services = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Scheme = "http";
        context.Request.Method = "POST";
        context.Request.Path = "/api/auth/login";
        context.Response.Body = new MemoryStream();
        var endpointReached = false;
        var middleware = new ApiTransportSecurityMiddleware(_ =>
        {
            endpointReached = true;
            return Task.CompletedTask;
        }, new TestEnvironment { EnvironmentName = environment });

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        endpointReached.Should().BeFalse();
        context.Response.Headers.Location.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    [InlineData("Production")]
    public async Task Invoke_HttpsRequest_ShouldContinueToEndpoint(string environment)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        var endpointReached = false;
        var middleware = new ApiTransportSecurityMiddleware(_ =>
        {
            endpointReached = true;
            return Task.CompletedTask;
        }, new TestEnvironment { EnvironmentName = environment });

        await middleware.InvokeAsync(context);

        endpointReached.Should().BeTrue();
    }

    [Fact]
    public async Task Invoke_TestServerHttp_ShouldAllowIsolatedBusinessTests()
    {
        var endpointReached = false;
        var middleware = new ApiTransportSecurityMiddleware(_ =>
        {
            endpointReached = true;
            return Task.CompletedTask;
        }, new TestEnvironment { EnvironmentName = "Testing" });

        await middleware.InvokeAsync(new DefaultHttpContext());

        endpointReached.Should().BeTrue();
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "TransportSecurityTests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
