using System.Security.Claims;
using FluentAssertions;
using HospitalInformationSystem.Api.Security;
using HospitalInformationSystem.Contracts.Security;
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
}
