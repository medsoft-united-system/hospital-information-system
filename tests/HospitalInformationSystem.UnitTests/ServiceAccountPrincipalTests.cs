using System.Security.Claims;
using FluentAssertions;
using HospitalInformationSystem.Api.Security;
using HospitalInformationSystem.Models.Common;
using HospitalInformationSystem.Models.ServiceAuth;
using Microsoft.AspNetCore.Http;

namespace HospitalInformationSystem.UnitTests;

public sealed class ServiceAccountPrincipalTests
{
    private static readonly Guid ServiceId = Guid.NewGuid();

    [Fact]
    public void ValidIdentity_UsesOnlyDedicatedServiceClaims()
    {
        var human = new ClaimsIdentity([new Claim("sub", Guid.NewGuid().ToString("D")),
            new Claim("name", "Администратор")], "Bearer");
        var principal = new ClaimsPrincipal([human, Identity()]);

        var identity = ServiceAccountPrincipal.TryGetIdentity(principal);

        identity.Should().NotBeNull();
        identity!.Id.Should().Be(ServiceId);
        identity.Name.Should().Be("go-lis");
        identity.AuthenticationVersion.Should().Be(3);
        new CurrentServiceAccount(new HttpContextAccessor
            { HttpContext = new DefaultHttpContext { User = principal } }).RequireIdentity().Should().Be(identity);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer")]
    public void UnauthenticatedOrHumanIdentity_CannotBecomeAService(string? authenticationType)
    {
        ServiceAccountPrincipal.TryGetIdentity(new ClaimsPrincipal(Identity(authenticationType)))
            .Should().BeNull();
    }

    [Theory]
    [InlineData("sub")]
    [InlineData("account_kind")]
    [InlineData("role")]
    [InlineData("auth_ver")]
    public void MissingOrDuplicatedSecurityClaim_IsRejected(string type)
    {
        var missing = Identity();
        missing.RemoveClaim(missing.FindFirst(type)!);
        ServiceAccountPrincipal.TryGetIdentity(new ClaimsPrincipal(missing)).Should().BeNull();
        var duplicate = Identity();
        duplicate.AddClaim(new Claim(type, duplicate.FindFirst(type)!.Value));
        ServiceAccountPrincipal.TryGetIdentity(new ClaimsPrincipal(duplicate)).Should().BeNull();
    }

    [Theory]
    [InlineData("sub", "00000000-0000-0000-0000-000000000000")]
    [InlineData("sub", "not-an-id")]
    [InlineData("account_kind", "human")]
    [InlineData("role", "Administrator")]
    [InlineData("role", "Other")]
    [InlineData("auth_ver", "0")]
    [InlineData("auth_ver", "-1")]
    [InlineData("auth_ver", "1.0")]
    [InlineData("auth_ver", "9223372036854775808")]
    [InlineData("name", " ")]
    [InlineData("name", "LIS\nspoof")]
    public void InvalidServiceClaims_AreRejected(string type, string value)
    {
        var identity = Identity();
        identity.RemoveClaim(identity.FindFirst(type)!);
        identity.AddClaim(new Claim(type, value));
        ServiceAccountPrincipal.TryGetIdentity(new ClaimsPrincipal(identity)).Should().BeNull();
    }

    [Fact]
    public void PartialClaimsAcrossMultipleIdentities_CannotBeCombined()
    {
        var first = Identity();
        first.RemoveClaim(first.FindFirst("role")!);
        var second = new ClaimsIdentity([new Claim("role", "LIS")], ServiceAuthenticationDefaults.Scheme);
        ServiceAccountPrincipal.TryGetIdentity(new ClaimsPrincipal([first, second])).Should().BeNull();
    }

    [Fact]
    public void MissingOptionalName_HasTechnicalDisplayName()
    {
        var identity = Identity();
        identity.RemoveClaim(identity.FindFirst("name")!);
        ServiceAccountPrincipal.TryGetIdentity(new ClaimsPrincipal(identity))!.Name.Should()
            .Be("Сервисный аккаунт LIS");
    }

    [Fact]
    public void MissingRequestOrServicePrincipal_DeniesCurrentServiceAccess()
    {
        foreach (var context in new HttpContext?[] { null, new DefaultHttpContext() })
        {
            var service = new CurrentServiceAccount(new HttpContextAccessor { HttpContext = context });
            var require = () => service.RequireIdentity();
            require.Should().Throw<ServiceAccountAccessDeniedException>();
        }
    }

    private static ClaimsIdentity Identity(string? authenticationType = ServiceAuthenticationDefaults.Scheme) => new(
    [
        new Claim("sub", ServiceId.ToString("D")), new Claim("account_kind", "service"),
        new Claim("role", "LIS"), new Claim("auth_ver", "3"), new Claim("name", "go-lis")
    ], authenticationType);
}
