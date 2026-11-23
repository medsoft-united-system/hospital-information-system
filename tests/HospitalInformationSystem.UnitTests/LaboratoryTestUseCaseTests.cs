using System.Text.Json;
using FluentAssertions;
using HospitalInformationSystem.Application.Abstractions.Repositories;
using HospitalInformationSystem.Application.Abstractions.UnitOfWork;
using HospitalInformationSystem.Application.LaboratoryTests.Commands.CreateLaboratoryTest;
using HospitalInformationSystem.Application.LaboratoryTests.Commands.SetLaboratoryTestActive;
using HospitalInformationSystem.Application.LaboratoryTests.Commands.UpdateLaboratoryTest;
using HospitalInformationSystem.Application.LaboratoryTests.Factories;
using HospitalInformationSystem.Application.LaboratoryTests.Queries.GetLaboratoryTest;
using HospitalInformationSystem.Application.LaboratoryTests.Queries.GetLaboratoryTests;
using HospitalInformationSystem.Application.Models.Common;
using HospitalInformationSystem.Application.Models.LaboratoryTests;
using Moq;

namespace HospitalInformationSystem.UnitTests;

public sealed class LaboratoryTestUseCaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Create_UsesNormalizedCodeAndInjectedTime()
    {
        var repository = new Mock<ILaboratoryTestRepository>();
        var uow = new Mock<IUnitOfWork>();
        var clock = new Mock<TimeProvider>();
        clock.Setup(x => x.GetUtcNow()).Returns(Now);
        LaboratoryTestDefinition? saved = null;
        repository.Setup(x => x.Add(It.IsAny<LaboratoryTestDefinition>()))
            .Callback<LaboratoryTestDefinition>(x =>
            {
                x.Version = 17;
                saved = x;
            });
        repository.Setup(x => x.FindAsync(It.IsAny<Guid>(), false, CancellationToken.None))
            .ReturnsAsync(() => saved);
        var handler = new CreateLaboratoryTestCommandHandler(repository.Object, uow.Object, clock.Object);

        var result = await handler.Handle(new CreateLaboratoryTestCommand(" 2345-7 ", " Глюкоза "), CancellationToken.None);

        repository.Verify(x => x.ExistsCodeAsync("2345-7", null, CancellationToken.None), Times.Once);
        saved.Should().NotBeNull();
        result.Id.Should().Be(saved!.Id);
        result.CreatedAtUtc.Should().Be(Now);
        result.UpdatedAtUtc.Should().Be(Now);
        result.IsActive.Should().BeTrue();
        result.DisplayName.Should().Be("Глюкоза");
        result.Version.Should().Be(17);
        uow.Verify(x => x.SaveChangesAsync(CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task Create_DuplicateCodeDoesNotAddOrSave()
    {
        var repository = new Mock<ILaboratoryTestRepository>();
        repository.Setup(x => x.ExistsCodeAsync("2345-7", null, CancellationToken.None)).ReturnsAsync(true);
        var uow = new Mock<IUnitOfWork>();
        var handler = new CreateLaboratoryTestCommandHandler(repository.Object, uow.Object, TimeProvider.System);

        var create = () => handler.Handle(new CreateLaboratoryTestCommand(" 2345-7 ", "Глюкоза"), CancellationToken.None);

        await create.Should().ThrowAsync<BusinessConflictException>();
        repository.Verify(x => x.Add(It.IsAny<LaboratoryTestDefinition>()), Times.Never);
        uow.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Update_DuplicateCodeDoesNotPersistChanges()
    {
        var definition = LaboratoryTestDefinitionFactory.Create("718-7", "Гемоглобин", Now);
        definition.Version = 1;
        var repository = new Mock<ILaboratoryTestRepository>();
        repository.Setup(x => x.FindAsync(definition.Id, true, CancellationToken.None)).ReturnsAsync(definition);
        repository.Setup(x => x.ExistsCodeAsync("2345-7", definition.Id, CancellationToken.None)).ReturnsAsync(true);
        var uow = new Mock<IUnitOfWork>();
        var handler = new UpdateLaboratoryTestCommandHandler(repository.Object, uow.Object, TimeProvider.System);

        var update = () => handler.Handle(new UpdateLaboratoryTestCommand(definition.Id, "2345-7", "Глюкоза", definition.Version), CancellationToken.None);

        await update.Should().ThrowAsync<BusinessConflictException>();
        repository.Verify(x => x.UpdateAsync(It.IsAny<LaboratoryTestDefinition>(), It.IsAny<CancellationToken>()), Times.Never);
        uow.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UnknownId_IsNotFoundForReadingUpdatingAndChangingActivity()
    {
        var repository = Mock.Of<ILaboratoryTestRepository>();
        var uow = new Mock<IUnitOfWork>();
        var id = Guid.NewGuid();
        var get = () => new GetLaboratoryTestQueryHandler(repository).Handle(new GetLaboratoryTestQuery(id), CancellationToken.None);
        var update = () => new UpdateLaboratoryTestCommandHandler(repository, uow.Object, TimeProvider.System)
            .Handle(new UpdateLaboratoryTestCommand(id, "2345-7", "Глюкоза", 1), CancellationToken.None);
        var active = () => new SetLaboratoryTestActiveCommandHandler(repository, uow.Object, TimeProvider.System)
            .Handle(new SetLaboratoryTestActiveCommand(id, false, 1), CancellationToken.None);

        await get.Should().ThrowAsync<EntityNotFoundException>();
        await update.Should().ThrowAsync<EntityNotFoundException>();
        await active.Should().ThrowAsync<EntityNotFoundException>();
        uow.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Deactivate_KeepsDefinitionAndStampsChangeTime()
    {
        var definition = LaboratoryTestDefinitionFactory.Create("2345-7", "Глюкоза", Now.AddDays(-1));
        definition.Version = 1;
        var repository = new Mock<ILaboratoryTestRepository>();
        repository.Setup(x => x.FindAsync(definition.Id, true, CancellationToken.None)).ReturnsAsync(definition);
        repository.Setup(x => x.FindAsync(definition.Id, false, CancellationToken.None)).ReturnsAsync(definition);
        var uow = new Mock<IUnitOfWork>();
        var clock = new Mock<TimeProvider>();
        clock.Setup(x => x.GetUtcNow()).Returns(Now);

        var result = await new SetLaboratoryTestActiveCommandHandler(repository.Object, uow.Object, clock.Object)
            .Handle(new SetLaboratoryTestActiveCommand(definition.Id, false, definition.Version), CancellationToken.None);

        result.IsActive.Should().BeFalse();
        result.CreatedAtUtc.Should().Be(Now.AddDays(-1));
        result.UpdatedAtUtc.Should().Be(Now);
        result.LoincCode.Should().Be("2345-7");
        repository.Verify(x => x.UpdateAsync(definition, CancellationToken.None), Times.Once);
        uow.Verify(x => x.SaveChangesAsync(CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task RepeatingActivityState_DoesNotWriteOrReadClock()
    {
        var definition = LaboratoryTestDefinitionFactory.Create("2345-7", "Глюкоза", Now);
        definition.Version = 1;
        var repository = new Mock<ILaboratoryTestRepository>();
        repository.Setup(x => x.FindAsync(definition.Id, true, CancellationToken.None)).ReturnsAsync(definition);
        var uow = new Mock<IUnitOfWork>();
        var clock = new Mock<TimeProvider>(MockBehavior.Strict);

        var result = await new SetLaboratoryTestActiveCommandHandler(repository.Object, uow.Object, clock.Object)
            .Handle(new SetLaboratoryTestActiveCommand(definition.Id, true, definition.Version), CancellationToken.None);

        result.UpdatedAtUtc.Should().Be(Now);
        repository.Verify(x => x.UpdateAsync(It.IsAny<LaboratoryTestDefinition>(), It.IsAny<CancellationToken>()), Times.Never);
        uow.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("UPDATE")]
    [InlineData("DEACTIVATE")]
    [InlineData("SAME_ACTIVITY")]
    public async Task StaleVersion_DoesNotMutateDefinitionOrPersistChanges(string operation)
    {
        var definition = LaboratoryTestDefinitionFactory.Create("2345-7", "Глюкоза", Now);
        definition.Version = 2;
        var repository = new Mock<ILaboratoryTestRepository>();
        repository.Setup(x => x.FindAsync(definition.Id, true, CancellationToken.None)).ReturnsAsync(definition);
        var uow = new Mock<IUnitOfWork>();
        var clock = new Mock<TimeProvider>(MockBehavior.Strict);

        Func<Task> mutation = operation == "UPDATE"
            ? () => new UpdateLaboratoryTestCommandHandler(repository.Object, uow.Object, clock.Object)
                .Handle(new UpdateLaboratoryTestCommand(definition.Id, "718-7", "Гемоглобин", 1), CancellationToken.None)
            : () => new SetLaboratoryTestActiveCommandHandler(repository.Object, uow.Object, clock.Object)
                .Handle(new SetLaboratoryTestActiveCommand(definition.Id, operation == "SAME_ACTIVITY", 1), CancellationToken.None);

        await mutation.Should().ThrowAsync<BusinessConflictException>();
        definition.LoincCode.Should().Be("2345-7");
        definition.DisplayName.Should().Be("Глюкоза");
        definition.IsActive.Should().BeTrue();
        definition.UpdatedAtUtc.Should().Be(Now);
        definition.Version.Should().Be(2);
        repository.Verify(x => x.UpdateAsync(It.IsAny<LaboratoryTestDefinition>(), It.IsAny<CancellationToken>()), Times.Never);
        uow.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MissingVersion_IsRejectedByValidatorsAndHandlersBeforeReadingRepository()
    {
        var update = new UpdateLaboratoryTestCommand(Guid.NewGuid(), "2345-7", "Глюкоза");
        var active = new SetLaboratoryTestActiveCommand(update.Id, false);
        (await new UpdateLaboratoryTestCommandValidator().ValidateAsync(update))
            .Errors.Should().Contain(x => x.PropertyName == nameof(update.Version));
        (await new SetLaboratoryTestActiveCommandValidator().ValidateAsync(active))
            .Errors.Should().Contain(x => x.PropertyName == nameof(active.Version));
        var repository = new Mock<ILaboratoryTestRepository>(MockBehavior.Strict);
        var uow = new Mock<IUnitOfWork>(MockBehavior.Strict);
        Func<Task> updateWithoutVersion = () => new UpdateLaboratoryTestCommandHandler(
            repository.Object, uow.Object, TimeProvider.System).Handle(update, CancellationToken.None);
        Func<Task> activityWithoutVersion = () => new SetLaboratoryTestActiveCommandHandler(
            repository.Object, uow.Object, TimeProvider.System).Handle(active, CancellationToken.None);

        await updateWithoutVersion.Should().ThrowAsync<DomainValidationException>();
        await activityWithoutVersion.Should().ThrowAsync<DomainValidationException>();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task List_ForwardsActivityFilterAndCancellation(bool activeOnly)
    {
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var definition = LaboratoryTestDefinitionFactory.Create("2345-7", "Глюкоза", Now);
        var repository = new Mock<ILaboratoryTestRepository>();
        repository.Setup(x => x.ListAsync(activeOnly, token)).ReturnsAsync(new[] { definition });

        var result = await new GetLaboratoryTestsQueryHandler(repository.Object)
            .Handle(new GetLaboratoryTestsQuery(activeOnly), token);

        result.Should().ContainSingle().Which.Id.Should().Be(definition.Id);
        repository.Verify(x => x.ListAsync(activeOnly, token), Times.Once);
    }

    [Theory]
    [InlineData("bad", "Глюкоза", "LoincCode")]
    [InlineData("2345-8", "Глюкоза", "LoincCode")]
    [InlineData("2345-7", "аб", "DisplayName")]
    [InlineData("2345-7", "Глю\0коза", "DisplayName")]
    public async Task Validators_RejectMalformedCodeAndName(string code, string name, string property)
    {
        var create = await new CreateLaboratoryTestCommandValidator().ValidateAsync(new CreateLaboratoryTestCommand(code, name));
        var update = await new UpdateLaboratoryTestCommandValidator().ValidateAsync(new UpdateLaboratoryTestCommand(Guid.NewGuid(), code, name, 1));
        create.IsValid.Should().BeFalse();
        update.IsValid.Should().BeFalse();
        create.Errors.Should().Contain(x => x.PropertyName == property);
        update.Errors.Should().Contain(x => x.PropertyName == property);
    }

    [Fact]
    public async Task Validators_AcceptUnicodeBoundaryAndRejectEmptyUpdateIds()
    {
        var name = string.Concat(Enumerable.Repeat("🧪", 256));
        (await new CreateLaboratoryTestCommandValidator().ValidateAsync(new CreateLaboratoryTestCommand(" 2345-7 ", name)))
            .IsValid.Should().BeTrue();
        (await new UpdateLaboratoryTestCommandValidator().ValidateAsync(new UpdateLaboratoryTestCommand(Guid.Empty, "2345-7", "Глюкоза", 1)))
            .Errors.Should().Contain(x => x.PropertyName == "Id");
        (await new SetLaboratoryTestActiveCommandValidator().ValidateAsync(new SetLaboratoryTestActiveCommand(Guid.Empty, false, 1)))
            .Errors.Should().Contain(x => x.PropertyName == "Id");
    }

    [Fact]
    public void ActivityRequest_RequiresExplicitFlag()
    {
        var missing = () => JsonSerializer.Deserialize<SetLaboratoryTestActiveRequest>("{}", JsonSerializerOptions.Web);
        missing.Should().Throw<JsonException>();
        JsonSerializer.Deserialize<SetLaboratoryTestActiveRequest>("{\"isActive\":false}", JsonSerializerOptions.Web)!
            .IsActive.Should().BeFalse();
    }
}
