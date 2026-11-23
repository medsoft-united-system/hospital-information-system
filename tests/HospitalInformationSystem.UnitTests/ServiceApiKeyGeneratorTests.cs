using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using HospitalInformationSystem.Infrastructure.Identity;
using HospitalInformationSystem.Infrastructure.ServiceAccounts;
using HospitalInformationSystem.Models.ServiceAccounts;
using Microsoft.Extensions.Options;

namespace HospitalInformationSystem.UnitTests;

public sealed class ServiceApiKeyGeneratorTests
{
    private static readonly Guid AccountId = Guid.Parse("12345678-9abc-4def-8abc-123456789abc");

    [Fact]
    public void Generate_UsesAccountIdAndIndependentRandomSecretAndStoresOnlyItsHash()
    {
        var generator = Generator();
        var first = generator.Generate(AccountId);
        var second = generator.Generate(AccountId);

        first.ApiKey.Should().HaveLength(80).And.StartWith(ServiceApiKeyGenerator.Prefix(AccountId));
        first.ApiKey.Should().NotBe(second.ApiKey);
        Encoding.UTF8.GetString(first.Hash).Should().StartWith("$argon2id$v=19$m=19456,t=2,p=1$");
        first.Hash.Should().NotEqual(second.Hash);
        var secret = Convert.FromBase64String(first.ApiKey[37..].Replace('-', '+').Replace('_', '/') + "=");
        secret.Should().HaveCount(32);
        first.Hash.Should().NotEqual(SHA256.HashData(secret));
        var rehashed = generator.Hash(first.ApiKey);
        rehashed.Should().NotEqual(first.Hash);
        generator.Verify(first.ApiKey, AccountId, rehashed).Should().Be(ServiceApiKeyVerificationResult.Success);
        ServiceApiKeyGenerator.TryGetAccountId(first.ApiKey, out var parsed).Should().BeTrue();
        parsed.Should().Be(AccountId);
        generator.Verify(first.ApiKey, AccountId, first.Hash).Should().Be(ServiceApiKeyVerificationResult.Success);
        generator.Verify(first.ApiKey, AccountId, second.Hash).Should().Be(ServiceApiKeyVerificationResult.Failed);
        generator.Verify(first.ApiKey, Guid.NewGuid(), first.Hash).Should().Be(ServiceApiKeyVerificationResult.Failed);
        generator.Verify(first.ApiKey, AccountId, new byte[31]).Should().Be(ServiceApiKeyVerificationResult.Failed);
    }

    [Fact]
    public void Generate_RejectsEmptyAccountId()
    {
        var generate = () => new ServiceApiKeyGenerator().Generate(Guid.Empty);
        generate.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("other")]
    [InlineData("newline")]
    [InlineData("upper-id")]
    [InlineData("empty-id")]
    [InlineData("short-secret")]
    [InlineData("long-secret")]
    [InlineData("padding")]
    [InlineData("standard-base64")]
    [InlineData("noncanonical")]
    [InlineData("wrong-prefix")]
    public void ParseAndVerify_RejectMalformedKeysWithoutUsingCallerControlledIds(string? kind)
    {
        string? key = ServiceApiKeyGenerator.Prefix(AccountId) + new string('A', 43);
        key = kind switch
        {
            null => null,
            "" => "",
            "other" => "other",
            "newline" => key[..^1] + "\n",
            "upper-id" => "msa_" + AccountId.ToString("N").ToUpperInvariant() + "." + new string('A', 43),
            "empty-id" => ServiceApiKeyGenerator.Prefix(Guid.Empty) + new string('A', 43),
            "short-secret" => key[..^1],
            "long-secret" => key + "A",
            "padding" => key[..^1] + "=",
            "standard-base64" => key[..^1] + "/",
            "noncanonical" => key[..^1] + "B",
            "wrong-prefix" => "MSA_" + key[4..],
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        ServiceApiKeyGenerator.TryGetAccountId(key, out var id).Should().BeFalse();
        id.Should().BeEmpty();
        Generator().Verify(key, AccountId, SHA256.HashData(new byte[32])).Should().Be(ServiceApiKeyVerificationResult.Failed);
    }

    [Fact]
    public void Verify_LegacySha256UsesDecodedBinarySecretAndRequiresRehash()
    {
        var key = ServiceApiKeyGenerator.Prefix(AccountId) + new string('A', 43);

        Generator().Verify(key, AccountId, SHA256.HashData(new byte[32])).Should().Be(ServiceApiKeyVerificationResult.SuccessRehashNeeded);
        Generator().Verify(key, AccountId, SHA256.HashData(Encoding.UTF8.GetBytes(key)))
            .Should().Be(ServiceApiKeyVerificationResult.Failed);
    }

    [Fact]
    public void Verify_ChangedCosts_ShouldRequireRehashWithoutRejectingTheKey()
    {
        var original = Generator();
        var key = original.Generate(AccountId);
        var stronger = new ServiceApiKeyGenerator(Options.Create(new Argon2idOptions
        { MemorySizeKiB = 32768, Iterations = 2, Parallelism = 1 }));

        stronger.Verify(key.ApiKey, AccountId, key.Hash).Should().Be(ServiceApiKeyVerificationResult.SuccessRehashNeeded);
        var upgraded = stronger.Hash(key.ApiKey);
        stronger.Verify(key.ApiKey, AccountId, upgraded).Should().Be(ServiceApiKeyVerificationResult.Success);
    }

    [Theory]
    [InlineData("m=19456", "m=999999")]
    [InlineData("t=2", "t=0")]
    [InlineData("p=1", "p=0")]
    [InlineData("v=19", "v=16")]
    public void Verify_InvalidArgonParameters_ShouldReject(string original, string replacement)
    {
        var generator = Generator();
        var key = generator.Generate(AccountId);
        var corrupted = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(key.Hash).Replace(original, replacement));

        generator.Verify(key.ApiKey, AccountId, corrupted).Should().Be(ServiceApiKeyVerificationResult.Failed);
    }

    [Fact]
    public void Verify_InvalidUtf8OrUnknownHashFormat_ShouldReject()
    {
        var key = ServiceApiKeyGenerator.Prefix(AccountId) + new string('A', 43);
        var corrupted = new byte[100];
        "$argon2id$v=19$"u8.CopyTo(corrupted);
        corrupted[^1] = 0xff;

        Generator().Verify(key, AccountId, corrupted).Should().Be(ServiceApiKeyVerificationResult.Failed);
        Generator().Verify(key, AccountId, new byte[100]).Should().Be(ServiceApiKeyVerificationResult.Failed);
        var hash = () => Generator().Hash("invalid-key");
        hash.Should().Throw<ArgumentException>();
    }

    private static ServiceApiKeyGenerator Generator() => new(Options.Create(new Argon2idOptions
    { MemorySizeKiB = 19456, Iterations = 2, Parallelism = 1 }));

}
