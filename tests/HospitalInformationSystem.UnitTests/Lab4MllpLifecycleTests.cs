using FluentAssertions;
using HospitalInformationSystem.Api.Hl7.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HospitalInformationSystem.UnitTests;

public sealed class Lab4MllpLifecycleTests
{
    [Fact]
    public async Task DisabledListener_ShouldNotLoadCertificatesOrBindSocket()
    {
        var options = new Hl7ImportOptions
        {
            Enabled = false, ListenAddress = "not-an-address", Port = -1,
            ServerCertificate = null!, ClientTrust = null!, Limits = null!, Timeouts = null!
        };
        using var services = new ServiceCollection().BuildServiceProvider();
        using var listener = Listener(services, options);

        await listener.StartAsync(CancellationToken.None);
        await listener.StopAsync(CancellationToken.None);

        listener.BoundEndpoint.Should().BeNull();
        listener.ActiveConnectionCount.Should().Be(0);
    }

    [Fact]
    public async Task CancelledStartup_ShouldNotBindOrLoadCertificates()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        using var listener = Listener(services, new Hl7ImportOptions { Enabled = true });
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var start = () => listener.StartAsync(cancelled.Token);

        await start.Should().ThrowAsync<OperationCanceledException>();
        listener.BoundEndpoint.Should().BeNull();
        listener.ActiveConnectionCount.Should().Be(0);
    }

    [Fact]
    public async Task StopBeforeStartup_ShouldBeSafe()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        using var listener = Listener(services, new Hl7ImportOptions());

        await listener.StopAsync(CancellationToken.None);
        await listener.StopAsync(CancellationToken.None);

        listener.BoundEndpoint.Should().BeNull();
        listener.ActiveConnectionCount.Should().Be(0);
    }

    private static Hl7MllpListener Listener(ServiceProvider services, Hl7ImportOptions options) => new(
        Options.Create(options), services.GetRequiredService<IServiceScopeFactory>(),
        TimeProvider.System, NullLogger<Hl7MllpListener>.Instance);
}
