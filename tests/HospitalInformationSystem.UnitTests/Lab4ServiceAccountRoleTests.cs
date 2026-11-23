using System.Text.Json;
using FluentAssertions;
using HospitalInformationSystem.Application.Abstractions.Repositories;
using HospitalInformationSystem.Application.Abstractions.Transactions;
using HospitalInformationSystem.Application.Abstractions.UnitOfWork;
using HospitalInformationSystem.Application.Contracts.Abstractions;
using HospitalInformationSystem.Application.Contracts.Audit;
using HospitalInformationSystem.Application.Models.Audit;
using HospitalInformationSystem.Application.Models.Common;
using HospitalInformationSystem.Application.Models.Persistence;
using HospitalInformationSystem.Application.Models.ServiceAccounts;
using HospitalInformationSystem.Application.ServiceAccounts.Factories;
using HospitalInformationSystem.Infrastructure.Contexts;
using HospitalInformationSystem.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Moq;
using DomainService = HospitalInformationSystem.Application.ServiceAccounts.Services.ServiceAccountDomainService;
using ManagementService = HospitalInformationSystem.Application.ServiceAccounts.Services.ServiceAccountService;

namespace HospitalInformationSystem.UnitTests;

public sealed class Lab4ServiceAccountRoleTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(ServiceAccountRole.Lis)]
    [InlineData(ServiceAccountRole.Hl7Import)]
    public void Factory_ExplicitRole_ShouldAssignRoleInBothOverloads(ServiceAccountRole role)
    {
        ServiceAccountFactory.Create("go-service", new byte[32], Now, role).Role.Should().Be(role);
        ServiceAccountFactory.Create(Guid.NewGuid(), "go-service", new byte[32], Now, role).Role.Should().Be(role);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(int.MaxValue)]
    public void Factory_UnknownRole_ShouldRejectInBothOverloads(int value)
    {
        var role = (ServiceAccountRole)value;
        var generatedId = () => ServiceAccountFactory.Create("go-service", new byte[32], Now, role);
        var explicitId = () => ServiceAccountFactory.Create(Guid.NewGuid(), "go-service", new byte[32], Now, role);

        generatedId.Should().Throw<DomainValidationException>();
        explicitId.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void LegacyConstructorsAndJson_ShouldDefaultToLis()
    {
        new CreateServiceAccountRequest("go-lis").Role.Should().Be(ServiceAccountRoles.Lis);
        JsonSerializer.Deserialize<CreateServiceAccountRequest>("""{"Name":"go-lis"}""")!
            .Role.Should().Be(ServiceAccountRoles.Lis);
        new ServiceAccountIdentity(Guid.NewGuid(), "go-lis", 1).Role.Should().Be(ServiceAccountRole.Lis);
        ServiceAccountFactory.Create("go-lis", new byte[32], Now).Role.Should().Be(ServiceAccountRole.Lis);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("lis", false)]
    [InlineData("HL7IMPORT", false)]
    [InlineData("Hl7Import", false)]
    [InlineData(" HL7Import ", false)]
    [InlineData("Administrator", false)]
    [InlineData(ServiceAccountRoles.Lis, true)]
    [InlineData(ServiceAccountRoles.Hl7Import, true)]
    public void ContractRoles_ShouldAcceptOnlyCanonicalRoles(string? role, bool supported)
    {
        ServiceAccountRoles.IsSupported(role).Should().Be(supported);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("lis")]
    [InlineData("HL7IMPORT")]
    [InlineData(" HL7Import ")]
    [InlineData("Administrator")]
    public async Task ManagementCreate_InvalidRole_ShouldRejectBeforeDatabaseOrKeyGeneration(string? role)
    {
        var repository = new Mock<IServiceAccountRepository>(MockBehavior.Strict);
        var unitOfWork = new Mock<IUnitOfWork>(MockBehavior.Strict);
        var transactions = new Mock<ITransactionManager>(MockBehavior.Strict);
        var keys = new Mock<IServiceApiKeyGenerator>(MockBehavior.Strict);
        var sessions = new Mock<IServiceSessionRevoker>(MockBehavior.Strict);
        var audit = new Mock<IAuditWriter>(MockBehavior.Strict);
        var service = new ManagementService(repository.Object, unitOfWork.Object, transactions.Object,
            TimeProvider.System, keys.Object, sessions.Object, audit.Object);

        var create = () => service.CreateAsync(new CreateServiceAccountRequest("go-service", role!),
            CancellationToken.None);

        await create.Should().ThrowAsync<DomainValidationException>();
        repository.VerifyNoOtherCalls();
        unitOfWork.VerifyNoOtherCalls();
        transactions.VerifyNoOtherCalls();
        keys.VerifyNoOtherCalls();
        sessions.VerifyNoOtherCalls();
        audit.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(ServiceAccountRole.Lis, ServiceAccountRoles.Lis)]
    [InlineData(ServiceAccountRole.Hl7Import, ServiceAccountRoles.Hl7Import)]
    public void PersistenceRoleConversion_ShouldRoundTripCanonicalRoles(ServiceAccountRole role, string stored)
    {
        var model = new ModelBuilder();
        var entity = model.Entity<ServiceAccountDbo>();
        new ServiceAccountConfiguration().Configure(entity);
        var converter = entity.Property(account => account.Role).Metadata.GetValueConverter()!;

        converter.ConvertToProvider(role).Should().Be(stored);
        converter.ConvertFromProvider(stored).Should().Be(role);
        entity.Metadata.GetCheckConstraints().Single(constraint => constraint.Name == "ck_service_accounts_role")
            .Sql.Should().Be("role IN ('LIS', 'HL7Import')");
    }

    [Theory]
    [InlineData("lis")]
    [InlineData("HL7IMPORT")]
    [InlineData("Administrator")]
    [InlineData("")]
    public void PersistenceRoleConversion_UnknownRole_ShouldReject(string stored)
    {
        var model = new ModelBuilder();
        var entity = model.Entity<ServiceAccountDbo>();
        new ServiceAccountConfiguration().Configure(entity);
        var converter = entity.Property(account => account.Role).Metadata.GetValueConverter()!;
        var read = () => converter.ConvertFromProvider(stored);
        var write = () => converter.ConvertToProvider((ServiceAccountRole)999);

        read.Should().Throw<InvalidOperationException>();
        write.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void DomainMutations_ShouldPreserveHl7ImportRole()
    {
        var account = ServiceAccountFactory.Create("go-hl7", new byte[32], Now, ServiceAccountRole.Hl7Import);

        DomainService.Rename(account, "renamed-hl7", Now.AddMinutes(1));
        DomainService.SetActive(account, false, Now.AddMinutes(2));
        DomainService.RotateKey(account, Enumerable.Repeat((byte)7, 32).ToArray(), Now.AddMinutes(3));
        DomainService.SetActive(account, true, Now.AddMinutes(4));

        account.Role.Should().Be(ServiceAccountRole.Hl7Import);
        account.Name.Should().Be("renamed-hl7");
        account.IsActive.Should().BeTrue();
        account.AuthenticationVersion.Should().Be(3);
    }
}
