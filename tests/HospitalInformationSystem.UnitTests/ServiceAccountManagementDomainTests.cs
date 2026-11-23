using FluentAssertions;
using HospitalInformationSystem.Application.Models.Common;
using HospitalInformationSystem.Application.Models.ServiceAccounts;
using HospitalInformationSystem.Application.ServiceAccounts.Factories;
using DomainService = HospitalInformationSystem.Application.ServiceAccounts.Services.ServiceAccountDomainService;

namespace HospitalInformationSystem.UnitTests;

public sealed class ServiceAccountManagementDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 10, 11, 12, TimeSpan.Zero);

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData("ab", false)]
    [InlineData(" lis ", true)]
    [InlineData("Лаборатория HIS", true)]
    [InlineData("lab\0lis", false)]
    [InlineData("lab\nlis", false)]
    [InlineData("lab\tlis", false)]
    [InlineData("lab\u0085lis", false)]
    public void NameRules_AgreeWithPublicContractAndRejectMalformedText(string? name, bool valid)
    {
        ServiceAccountRules.IsValidName(name).Should().Be(valid);
        ServiceAccountNameRules.IsValid(name).Should().Be(valid);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NameRules_RejectUnpairedUtf16Surrogate(bool high)
    {
        var name = "lab" + (high ? '\ud800' : '\udc00') + "lis";
        ServiceAccountRules.IsValidName(name).Should().BeFalse();
        ServiceAccountNameRules.IsValid(name).Should().BeFalse();
        var create = () => ServiceAccountFactory.Create(name, new byte[32], Now);
        create.Should().Throw<DomainValidationException>();
    }

    [Theory]
    [InlineData(3, true)]
    [InlineData(64, true)]
    [InlineData(65, false)]
    public void NameLimits_CountUnicodeScalarsInBothLayers(int count, bool valid)
    {
        var name = string.Concat(Enumerable.Repeat("🧪", count));
        ServiceAccountRules.IsValidName(name).Should().Be(valid);
        ServiceAccountNameRules.IsValid(name).Should().Be(valid);
        if (valid)
            ServiceAccountFactory.Create(name, new byte[32], Now).Name.Should().Be(name);
    }

    [Fact]
    public void Rename_PreservesKeyAndSecurityVersionAndMakesNoOpExplicit()
    {
        var account = Account();
        var hash = account.ApiKeyHash.ToArray();
        DomainService.Rename(account, " Renamed LIS ", Now.AddMinutes(1)).Should().BeTrue();
        account.Name.Should().Be("Renamed LIS");
        account.NormalizedName.Should().Be("RENAMED LIS");
        account.AuthenticationVersion.Should().Be(1);
        account.ApiKeyHash.Should().Equal(hash);
        account.UpdatedAtUtc.Should().Be(Now.AddMinutes(1));
        DomainService.Rename(account, " Renamed LIS ", Now.AddMinutes(2)).Should().BeFalse();
        account.UpdatedAtUtc.Should().Be(Now.AddMinutes(1));
    }

    [Fact]
    public void DisableAndReactivate_DoesNotRestorePreviousSecurityVersion()
    {
        var account = Account();
        DomainService.SetActive(account, false, Now.AddMinutes(1)).Should().BeTrue();
        account.IsActive.Should().BeFalse();
        account.AuthenticationVersion.Should().Be(2);
        DomainService.SetActive(account, false, Now.AddMinutes(2)).Should().BeFalse();
        account.AuthenticationVersion.Should().Be(2);
        account.UpdatedAtUtc.Should().Be(Now.AddMinutes(1));
        DomainService.SetActive(account, true, Now.AddMinutes(3)).Should().BeTrue();
        account.IsActive.Should().BeTrue();
        account.AuthenticationVersion.Should().Be(2);
    }

    [Fact]
    public void RotateKey_CopiesHashAndAdvancesVersionWithoutReactivatingDisabledAccount()
    {
        var account = Account();
        DomainService.SetActive(account, false, Now);
        var hash = Enumerable.Repeat((byte)7, 32).ToArray();

        DomainService.RotateKey(account, hash, Now.AddMinutes(1));

        account.ApiKeyHash.Should().Equal(hash);
        hash[0] = 0;
        account.ApiKeyHash[0].Should().Be(7);
        account.AuthenticationVersion.Should().Be(3);
        account.IsActive.Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SecurityVersionOverflow_DoesNotPartiallyMutateAccount(bool rotate)
    {
        var account = Account();
        account.AuthenticationVersion = long.MaxValue;
        var change = () =>
        {
            if (rotate) DomainService.RotateKey(account, Enumerable.Repeat((byte)8, 32).ToArray(), Now.AddMinutes(1));
            else DomainService.SetActive(account, false, Now.AddMinutes(1));
        };

        change.Should().Throw<BusinessRuleConflictException>();
        account.IsActive.Should().BeTrue();
        account.ApiKeyHash.Should().Equal(new byte[32]);
        account.AuthenticationVersion.Should().Be(long.MaxValue);
        account.UpdatedAtUtc.Should().Be(Now);
    }

    [Fact]
    public void Mutation_ClockGoingBackwards_DoesNotRegressStoredTimestamp()
    {
        var account = Account();
        account.UpdatedAtUtc = Now.AddMinutes(2);
        DomainService.Rename(account, "Another LIS", Now.AddMinutes(-1));
        account.UpdatedAtUtc.Should().Be(Now.AddMinutes(2));
        DomainService.RotateKey(account, Enumerable.Repeat((byte)3, 32).ToArray(), Now.AddMinutes(-5));
        account.UpdatedAtUtc.Should().Be(Now.AddMinutes(2));
    }

    [Fact]
    public void InvalidRenameAndKey_DoNotChangeStoredState()
    {
        var account = Account();
        var rename = () => DomainService.Rename(account, "ab", Now.AddMinutes(1));
        var rotate = () => DomainService.RotateKey(account, new byte[31], Now.AddMinutes(1));
        rename.Should().Throw<DomainValidationException>();
        rotate.Should().Throw<DomainValidationException>();
        account.Name.Should().Be("go-lis");
        account.AuthenticationVersion.Should().Be(1);
        account.UpdatedAtUtc.Should().Be(Now);
    }

    [Fact]
    public void Factory_KeepsExplicitAccountIdAndPostgresPrecision()
    {
        var id = Guid.NewGuid();
        var account = ServiceAccountFactory.Create(id, "go-lis", new byte[32], Now.AddTicks(17));
        account.Id.Should().Be(id);
        account.CreatedAtUtc.Should().Be(Now.AddTicks(10));
        account.UpdatedAtUtc.Should().Be(account.CreatedAtUtc);
        var create = () => ServiceAccountFactory.Create(Guid.Empty, "go-lis", new byte[32], Now);
        create.Should().Throw<DomainValidationException>();
    }

    private static ServiceAccount Account() => ServiceAccountFactory.Create("go-lis", new byte[32], Now);
}
