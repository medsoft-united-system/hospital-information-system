using System.Security.Claims;
using FluentAssertions;
using HospitalInformationSystem.Api.Security;
using HospitalInformationSystem.Application.Models.Security;
using HospitalInformationSystem.Application.Models.ServiceAuth;
using Microsoft.AspNetCore.Http;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class AuditActorAccessorTests
{
    [Theory]
    [InlineData(ClaimTypes.NameIdentifier)]
    [InlineData("sub")]
    public void GetCurrentActor_AuthenticatedRequest_ShouldUseVerifiedUserAndRequestId(string identifierClaim)
    {
        var userId = Guid.NewGuid();
        var context = new DefaultHttpContext
        {
            TraceIdentifier = "verified-request",
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(identifierClaim, userId.ToString()), new Claim(ClaimTypes.Name, "admin") }, "test"))
        };
        var accessor = new HttpAuditActorAccessor(new HttpContextAccessor { HttpContext = context });

        var actor = accessor.GetCurrentActor();

        actor.Kind.Should().Be(AuditActorKind.User);
        actor.UserId.Should().Be(userId);
        actor.DisplayName.Should().Be("admin");
        actor.RequestId.Should().Be("verified-request");
        actor.Component.Should().BeNull();
    }

    [Fact]
    public void GetCurrentActor_UnauthenticatedRequestWithForgedClaim_ShouldRemainAnonymous()
    {
        var context = new DefaultHttpContext
        {
            TraceIdentifier = "anonymous-request",
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new Claim(ClaimTypes.Name, "forged-admin")
            }))
        };
        var accessor = new HttpAuditActorAccessor(new HttpContextAccessor { HttpContext = context });

        var actor = accessor.GetCurrentActor();

        actor.Kind.Should().Be(AuditActorKind.Anonymous);
        actor.UserId.Should().BeNull();
        actor.DisplayName.Should().NotBe("forged-admin");
        actor.RequestId.Should().Be("anonymous-request");
    }

    [Fact]
    public void GetCurrentActor_AuthenticatedRequestWithoutValidUserId_ShouldRemainAnonymous()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "not-a-guid"), new Claim(ClaimTypes.Name, "admin")
            }, "test"))
        };
        var accessor = new HttpAuditActorAccessor(new HttpContextAccessor { HttpContext = context });

        var actor = accessor.GetCurrentActor();

        actor.Kind.Should().Be(AuditActorKind.Anonymous);
        actor.UserId.Should().BeNull();
    }

    [Fact]
    public void GetCurrentActor_NoHttpRequest_ShouldIdentifySystemComponent()
    {
        var accessor = new HttpAuditActorAccessor(new HttpContextAccessor { HttpContext = null });

        var actor = accessor.GetCurrentActor();

        actor.Kind.Should().Be(AuditActorKind.System);
        actor.UserId.Should().BeNull();
        actor.Component.Should().Be("HospitalInformationSystem");
    }

    [Fact]
    public void GetCurrentActor_ServiceBearer_ShouldUseSeparateVerifiedServiceIdentity()
    {
        var serviceId = Guid.NewGuid();
        var context = new DefaultHttpContext
        {
            TraceIdentifier = "service-request",
            User = ServicePrincipal(serviceId.ToString("D"), ServiceAuthenticationDefaults.Role,
                ServiceAuthenticationDefaults.Scheme)
        };
        var accessor = new HttpAuditActorAccessor(new HttpContextAccessor { HttpContext = context });

        var actor = accessor.GetCurrentActor();

        actor.Kind.Should().Be(AuditActorKind.ServiceAccount);
        actor.UserId.Should().BeNull();
        actor.ServiceAccountId.Should().Be(serviceId);
        actor.DisplayName.Should().Be("lis.external");
        actor.RequestId.Should().Be("service-request");
    }

    [Theory]
    [InlineData("not-a-guid", "LIS", "ServiceBearer")]
    [InlineData("55555555-5555-4555-8555-555555555555", "Other", "ServiceBearer")]
    [InlineData("55555555-5555-4555-8555-555555555555", "LIS", "Bearer")]
    public void GetCurrentActor_InvalidServiceIdentity_ShouldNotInventHumanActor(string subject, string role,
        string authenticationType)
    {
        var context = new DefaultHttpContext { User = ServicePrincipal(subject, role, authenticationType) };
        var accessor = new HttpAuditActorAccessor(new HttpContextAccessor { HttpContext = context });

        var actor = accessor.GetCurrentActor();

        actor.Kind.Should().Be(AuditActorKind.Anonymous);
        actor.UserId.Should().BeNull();
        actor.ServiceAccountId.Should().BeNull();
    }

    private static ClaimsPrincipal ServicePrincipal(string subject, string role, string authenticationType) =>
        new(new ClaimsIdentity(new[]
        {
            new Claim("sub", subject), new Claim("account_kind", "service"), new Claim("role", role),
            new Claim("auth_ver", "1"), new Claim("sid", Guid.NewGuid().ToString("D")),
            new Claim("name", "lis.external"), new Claim(ClaimTypes.Name, "lis.external")
        }, authenticationType));
}
