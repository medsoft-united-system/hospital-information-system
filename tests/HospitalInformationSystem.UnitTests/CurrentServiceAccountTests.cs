using FluentAssertions;
using HospitalInformationSystem.Application.DI;
using HospitalInformationSystem.Application.Models.Common;
using HospitalInformationSystem.Application.Models.ServiceAccounts;
using HospitalInformationSystem.Application.Contracts.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalInformationSystem.UnitTests;

public sealed class CurrentServiceAccountTests
{
    [Fact]
    public void UnconfiguredServiceAuthentication_DeniesServiceIdentity()
    {
        using var services = new ServiceCollection().AddApplication().BuildServiceProvider();
        using var scope = services.CreateScope();
        var accessor = scope.ServiceProvider.GetRequiredService<ICurrentServiceAccount>();

        var getIdentity = accessor.RequireIdentity;

        getIdentity.Should().Throw<ServiceAccountAccessDeniedException>();
    }

    [Fact]
    public void ApplicationRegistration_PreservesExplicitServiceIdentityProvider()
    {
        var identity = new ServiceAccountIdentity(Guid.NewGuid(), "fixture-lis", 1);
        var registrations = new ServiceCollection();
        registrations.AddScoped<ICurrentServiceAccount>(_ => new FakeCurrentServiceAccount(identity));
        registrations.AddApplication();
        using var services = registrations.BuildServiceProvider();
        using var scope = services.CreateScope();

        scope.ServiceProvider.GetRequiredService<ICurrentServiceAccount>().RequireIdentity()
            .Should().BeSameAs(identity);
    }

    private sealed class FakeCurrentServiceAccount(ServiceAccountIdentity identity) : ICurrentServiceAccount
    {
        public ServiceAccountIdentity RequireIdentity() => identity;
    }
}
