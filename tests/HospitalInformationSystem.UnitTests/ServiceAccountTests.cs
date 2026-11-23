using FluentAssertions;
using HospitalInformationSystem.Application.Models.Common;
using HospitalInformationSystem.Application.Models.ServiceAccounts;
using HospitalInformationSystem.Application.ServiceAccounts.Factories;

namespace HospitalInformationSystem.UnitTests;

public sealed class ServiceAccountTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_ShouldNormalizeNameCopyHashAndAssignServiceState()
    {
        var hash = Enumerable.Range(0, 32).Select(x => (byte)x).ToArray();
        var account = ServiceAccountFactory.Create(" Lab-Gateway ", hash, Now.ToOffset(TimeSpan.FromHours(3)));
        account.Id.Should().NotBeEmpty();
        account.Name.Should().Be("Lab-Gateway");
        account.NormalizedName.Should().Be("LAB-GATEWAY");
        account.Role.Should().Be(ServiceAccountRole.Lis);
        account.IsActive.Should().BeTrue();
        account.AuthenticationVersion.Should().Be(1);
        account.CreatedAtUtc.Should().Be(Now);
        account.CreatedAtUtc.Offset.Should().Be(TimeSpan.Zero);
        account.UpdatedAtUtc.Should().Be(account.CreatedAtUtc);
        account.ApiKeyHash.Should().Equal(hash);
        hash[0] = 255;
        account.ApiKeyHash[0].Should().Be(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ab")]
    [InlineData("lis\0gateway")]
    public void Create_InvalidName_ShouldReject(string? name)
    {
        var create = () => ServiceAccountFactory.Create(name!, new byte[32], Now);
        create.Should().Throw<DomainValidationException>();
    }

    [Theory]
    [InlineData(3)]
    [InlineData(64)]
    public void Create_BoundaryNameLengths_ShouldAccept(int length)
    {
        ServiceAccountFactory.Create(new string('a', length), new byte[32], Now).Name.Length.Should().Be(length);
    }

    [Fact]
    public void Create_TooLongName_ShouldReject()
    {
        var create = () => ServiceAccountFactory.Create(new string('a', 65), new byte[32], Now);
        create.Should().Throw<DomainValidationException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public void Create_InvalidHashLength_ShouldReject(int length)
    {
        var create = () => ServiceAccountFactory.Create("lis", new byte[length], Now);
        create.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void Create_NullHash_ShouldReject()
    {
        var create = () => ServiceAccountFactory.Create("lis", null!, Now);
        create.Should().Throw<DomainValidationException>();
    }
}
