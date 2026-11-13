using FluentAssertions;
using HospitalInformationSystem.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using HospitalInformationSystem.Infrastructure.DI;

namespace HospitalInformationSystem.UnitTests;

public sealed class Argon2idPasswordHasherTests
{
    private readonly Argon2idPasswordHasher _hasher = new();
    private readonly HospitalUser _user = new();

    [Fact]
    public void HashPassword_SamePasswordTwice_ShouldUseIndependentSalts()
    {
        var first = _hasher.HashPassword(_user, "GeneratedPassword123");
        var second = _hasher.HashPassword(_user, "GeneratedPassword123");

        first.Should().StartWith("$argon2id$v=19$m=65536,t=3,p=1$");
        first.Should().NotBe(second);
        first.Should().NotContain("GeneratedPassword123");
        _hasher.VerifyHashedPassword(_user, first, "GeneratedPassword123")
            .Should().Be(PasswordVerificationResult.Success);
        _hasher.VerifyHashedPassword(_user, second, "GeneratedPassword123")
            .Should().Be(PasswordVerificationResult.Success);
    }

    [Fact]
    public void VerifyHashedPassword_WrongPassword_ShouldReject()
    {
        var hash = _hasher.HashPassword(_user, "ВерныйПароль123");

        _hasher.VerifyHashedPassword(_user, hash, "НеверныйПароль123")
            .Should().Be(PasswordVerificationResult.Failed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-password-hash")]
    [InlineData("$argon2id$v=19$m=2147483647,t=3,p=1$AAA$BBB")]
    [InlineData("$argon2id$v=19$m=65536,t=2147483647,p=1$AAA$BBB")]
    [InlineData("$argon2id$v=19$m=65536,t=3,p=0$AAA$BBB")]
    [InlineData("$argon2id$v=18$m=65536,t=3,p=1$AAA$BBB")]
    [InlineData("$argon2id$v=19$m=65536,t=3,p=1$%%%$BBB")]
    public void VerifyHashedPassword_MalformedOrExcessiveParameters_ShouldReject(string hash)
    {
        _hasher.VerifyHashedPassword(_user, hash, "Password123")
            .Should().Be(PasswordVerificationResult.Failed);
    }

    [Fact]
    public void VerifyHashedPassword_LegacyIdentityPassword_ShouldRequireRehashOnlyAfterSuccessfulVerification()
    {
        var hash = new PasswordHasher<HospitalUser>().HashPassword(_user, "LegacyPassword123");

        _hasher.VerifyHashedPassword(_user, hash, "LegacyPassword123")
            .Should().Be(PasswordVerificationResult.SuccessRehashNeeded);
        _hasher.VerifyHashedPassword(_user, hash, "WrongPassword123")
            .Should().Be(PasswordVerificationResult.Failed);
        _hasher.VerifyHashedSecret(hash, "LegacyPassword123")
            .Should().Be(PasswordVerificationResult.Failed);
    }

    [Fact]
    public void HashPassword_ConfiguredCosts_ShouldEncodeParametersAndVerifyUsingStoredValues()
    {
        var configured = new Argon2idPasswordHasher(Options.Create(new Argon2idOptions
        { MemorySizeKiB = 32768, Iterations = 2, Parallelism = 2 }));

        var hash = configured.HashPassword(_user, "ConfiguredPassword123");

        hash.Should().StartWith("$argon2id$v=19$m=32768,t=2,p=2$");
        configured.VerifyHashedPassword(_user, hash, "ConfiguredPassword123")
            .Should().Be(PasswordVerificationResult.Success);
        _hasher.VerifyHashedPassword(_user, hash, "ConfiguredPassword123")
            .Should().Be(PasswordVerificationResult.SuccessRehashNeeded);
    }

    [Theory]
    [InlineData(98304, 2)]
    [InlineData(32768, 4)]
    [InlineData(98304, 4)]
    public void VerifyHashedPassword_HigherStoredCost_ShouldNotDowngradeEitherCostDimension(int memory, int iterations)
    {
        var stronger = new Argon2idPasswordHasher(Options.Create(new Argon2idOptions
        { MemorySizeKiB = memory, Iterations = iterations }));
        var hash = stronger.HashPassword(_user, "PreservedPassword123");

        _hasher.VerifyHashedPassword(_user, hash, "PreservedPassword123")
            .Should().Be(PasswordVerificationResult.Success);
    }

    [Fact]
    public void ResolveHasher_ConfiguredInfrastructure_ShouldUseConfiguredParameters()
    {
        var services = new ServiceCollection();
        services.AddInfrastructure("Host=localhost;Database=unused;Username=unused");
        services.Configure<Argon2idOptions>(options => { options.MemorySizeKiB = 32768; options.Iterations = 2; });
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<HospitalUser>>();

        hasher.HashPassword(_user, "DependencyPassword123").Should().StartWith("$argon2id$v=19$m=32768,t=2,p=1$");
    }

    [Theory]
    [InlineData(19455, 3, 1)]
    [InlineData(262145, 3, 1)]
    [InlineData(65536, 1, 1)]
    [InlineData(65536, 11, 1)]
    [InlineData(65536, 3, 0)]
    [InlineData(65536, 3, 5)]
    public void CreateHasher_UnsafeOrExcessiveConfiguredCosts_ShouldRejectBeforeHashing(int memory, int iterations, int parallelism)
    {
        var create = () => new Argon2idPasswordHasher(Options.Create(new Argon2idOptions
        { MemorySizeKiB = memory, Iterations = iterations, Parallelism = parallelism }));

        create.Should().Throw<OptionsValidationException>();
    }
}
