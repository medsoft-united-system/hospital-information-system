using System.Security.Cryptography;
using FluentAssertions;
using HospitalInformationSystem.Infrastructure.ServiceAccounts;

namespace HospitalInformationSystem.UnitTests;

public sealed class ServiceApiKeyGeneratorTests
{
    private static readonly Guid AccountId = Guid.Parse("12345678-9abc-4def-8abc-123456789abc");

    [Fact]
    public void Generate_UsesAccountIdAndIndependentRandomSecretAndStoresOnlyItsHash()
    {
        var generator = new ServiceApiKeyGenerator();
        var first = generator.Generate(AccountId);
        var second = generator.Generate(AccountId);

        first.ApiKey.Should().HaveLength(80).And.StartWith(ServiceApiKeyGenerator.Prefix(AccountId));
        first.ApiKey.Should().NotBe(second.ApiKey);
        first.Hash.Should().HaveCount(32).And.NotEqual(second.Hash);
        var secret = Convert.FromBase64String(first.ApiKey[37..].Replace('-', '+').Replace('_', '/') + "=");
        secret.Should().HaveCount(32);
        first.Hash.Should().Equal(SHA256.HashData(secret));
        ServiceApiKeyGenerator.TryGetAccountId(first.ApiKey, out var parsed).Should().BeTrue();
        parsed.Should().Be(AccountId);
        ServiceApiKeyGenerator.Verify(first.ApiKey, AccountId, first.Hash).Should().BeTrue();
        ServiceApiKeyGenerator.Verify(first.ApiKey, AccountId, second.Hash).Should().BeFalse();
        ServiceApiKeyGenerator.Verify(first.ApiKey, Guid.NewGuid(), first.Hash).Should().BeFalse();
        ServiceApiKeyGenerator.Verify(first.ApiKey, AccountId, new byte[31]).Should().BeFalse();
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
        ServiceApiKeyGenerator.Verify(key, AccountId, SHA256.HashData(new byte[32])).Should().BeFalse();
    }

    [Fact]
    public void Verify_UsesDecodedBinarySecretRatherThanPublicPrefix()
    {
        var key = ServiceApiKeyGenerator.Prefix(AccountId) + new string('A', 43);

        ServiceApiKeyGenerator.Verify(key, AccountId, SHA256.HashData(new byte[32])).Should().BeTrue();
        ServiceApiKeyGenerator.Verify(key, AccountId, SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))
            .Should().BeFalse();
    }
}
