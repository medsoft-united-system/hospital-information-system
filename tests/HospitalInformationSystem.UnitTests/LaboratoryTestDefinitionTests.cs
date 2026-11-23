using FluentAssertions;
using HospitalInformationSystem.Application.LaboratoryTests.Factories;
using HospitalInformationSystem.Application.LaboratoryTests.Services;
using HospitalInformationSystem.Application.Models.Common;

namespace HospitalInformationSystem.UnitTests;

public sealed class LaboratoryTestDefinitionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_TrimsInputAndAssignsActiveIdentityWithUtcDates()
    {
        var definition = LaboratoryTestDefinitionFactory.Create(" 2345-7 ", " Глюкоза в сыворотке или плазме ",
            Now.ToOffset(TimeSpan.FromHours(3)));

        definition.Id.Should().NotBeEmpty();
        definition.LoincCode.Should().Be("2345-7");
        definition.DisplayName.Should().Be("Глюкоза в сыворотке или плазме");
        definition.IsActive.Should().BeTrue();
        definition.CreatedAtUtc.Should().Be(Now);
        definition.CreatedAtUtc.Offset.Should().Be(TimeSpan.Zero);
        definition.UpdatedAtUtc.Should().Be(definition.CreatedAtUtc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("2345")]
    [InlineData("2345-77")]
    [InlineData("2345-8")]
    [InlineData("2345--7")]
    [InlineData("２３４５-７")]
    [InlineData("LOINC:2345-7")]
    [InlineData("2345/7")]
    [InlineData("2345-7\0")]
    public void Create_InvalidCodeIsRejected(string? code)
    {
        var create = () => LaboratoryTestDefinitionFactory.Create(code!, "Глюкоза", Now);
        create.Should().Throw<DomainValidationException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" аб ")]
    [InlineData("Глю\0коза")]
    public void Create_InvalidDisplayNameIsRejected(string? name)
    {
        var create = () => LaboratoryTestDefinitionFactory.Create("2345-7", name!, Now);
        create.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void Create_DisplayNameBoundaryCountsUnicodeScalars()
    {
        var name = string.Concat(Enumerable.Repeat("🧪", 256));
        LaboratoryTestDefinitionFactory.Create("718-7", name, Now).DisplayName.Should().Be(name);
        var tooLong = () => LaboratoryTestDefinitionFactory.Create("718-7", name + "🧪", Now);
        tooLong.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void Create_CodeBoundaryIsIndependentOfDiagnosisCodeLength()
    {
        LaboratoryTestDefinitionFactory.Create("1-8", "Тест", Now).LoincCode.Should().Be("1-8");
        var maxCode = new string('1', 30) + "-5";
        LaboratoryTestDefinitionFactory.Create(maxCode, "Тест", Now).LoincCode.Should().Be(maxCode);
        var tooLong = () => LaboratoryTestDefinitionFactory.Create("1" + maxCode, "Тест", Now);
        tooLong.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void Update_PreservesIdentityCreationAndInactiveState()
    {
        var definition = LaboratoryTestDefinitionFactory.Create("2345-7", "Глюкоза", Now);
        var id = definition.Id;
        LaboratoryTestDefinitionService.SetActive(definition, false, Now.AddMinutes(1));
        LaboratoryTestDefinitionService.Update(definition, " 718-7 ", " Гемоглобин ", Now.AddMinutes(2));

        definition.Id.Should().Be(id);
        definition.CreatedAtUtc.Should().Be(Now);
        definition.UpdatedAtUtc.Should().Be(Now.AddMinutes(2));
        definition.LoincCode.Should().Be("718-7");
        definition.DisplayName.Should().Be("Гемоглобин");
        definition.IsActive.Should().BeFalse();
    }

    [Fact]
    public void InvalidUpdate_DoesNotPartiallyChangeDefinition()
    {
        var definition = LaboratoryTestDefinitionFactory.Create("2345-7", "Глюкоза", Now);
        var update = () => LaboratoryTestDefinitionService.Update(definition, "718-7", " ", Now.AddDays(1));
        update.Should().Throw<DomainValidationException>();
        definition.LoincCode.Should().Be("2345-7");
        definition.DisplayName.Should().Be("Глюкоза");
        definition.UpdatedAtUtc.Should().Be(Now);
    }

    [Fact]
    public void SetActive_RepeatedStateDoesNotChangeTimestamp()
    {
        var definition = LaboratoryTestDefinitionFactory.Create("2345-7", "Глюкоза", Now);
        LaboratoryTestDefinitionService.SetActive(definition, true, Now.AddMinutes(1));
        definition.UpdatedAtUtc.Should().Be(Now);
        LaboratoryTestDefinitionService.SetActive(definition, false, Now.AddMinutes(2));
        LaboratoryTestDefinitionService.SetActive(definition, false, Now.AddMinutes(3));
        definition.IsActive.Should().BeFalse();
        definition.UpdatedAtUtc.Should().Be(Now.AddMinutes(2));
    }
}
