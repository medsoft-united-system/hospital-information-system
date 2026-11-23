using System.Text.Json;
using FluentAssertions;
using HospitalInformationSystem.Application.Models.Maintenance;
using HospitalInformationSystem.Infrastructure.Identity;
using HospitalInformationSystem.Infrastructure.Maintenance;

namespace HospitalInformationSystem.UnitTests;

public sealed class PasswordHashingBenchmarkTests
{
    [Fact]
    public void Run_ValidConfiguration_ShouldMeasureBothOperationsWithoutIncludingCredentials()
    {
        var options = new Argon2idOptions { MemorySizeKiB = 19456, Iterations = 2, Parallelism = 1 };

        var result = PasswordHashingBenchmark.Run(options, 3, CancellationToken.None);
        var json = JsonSerializer.Serialize(result);

        result.Iterations.Should().Be(3);
        result.MemorySizeKiB.Should().Be(options.MemorySizeKiB);
        result.AlgorithmIterations.Should().Be(options.Iterations);
        result.Hash.SamplesMilliseconds.Should().HaveCount(3).And.OnlyContain(value => value > 0);
        result.Verify.SamplesMilliseconds.Should().HaveCount(3).And.OnlyContain(value => value > 0);
        result.Hash.P95Milliseconds.Should().Be(result.Hash.SamplesMilliseconds.Max());
        result.Verify.P95Milliseconds.Should().Be(result.Verify.SamplesMilliseconds.Max());
        result.Hash.MedianMilliseconds.Should().Be(result.Hash.SamplesMilliseconds.Order().ElementAt(1));
        json.Should().NotContain("$argon2id$").And.NotContain("PasswordHash");
        typeof(PasswordHashingBenchmarkResult).GetProperties().Should().NotContain(property =>
            property.Name == "Password" || property.Name == "PasswordHash" || property.Name == "Salt");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(51)]
    public void Run_InvalidSampleCount_ShouldRejectBeforeMeasurement(int iterations)
    {
        var run = () => PasswordHashingBenchmark.Run(new Argon2idOptions(), iterations, CancellationToken.None);

        run.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Run_CancelledToken_ShouldNotStartHashing()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var run = () => PasswordHashingBenchmark.Run(new Argon2idOptions(), 3, cancelled.Token);

        run.Should().Throw<OperationCanceledException>();
    }
}
