using FluentAssertions;
using HospitalInformationSystem.Application.LaboratoryTests.Factories;
using HospitalInformationSystem.Application.LaboratoryTests.Services;
using HospitalInformationSystem.Application.Models.Common;
using HospitalInformationSystem.Application.Models.LaboratoryTests;

namespace HospitalInformationSystem.UnitTests;

public sealed class LoincCodeValidationTests
{
    [Theory]
    [InlineData("2345-8")]
    [InlineData("1-1")]
    [InlineData("9999999-9")]
    public void LegacyNumericShape_ShouldRemainRecognizableWithoutPassingCatalogValidation(string code)
    {
        LaboratoryTestDefinitionRules.IsWellFormedCode(code).Should().BeTrue();
        LaboratoryTestDefinitionRules.IsValidCode(code).Should().BeFalse();
        LaboratoryTestCodeRules.IsValidCode(code).Should().BeFalse();
        var create = () => LaboratoryTestDefinitionFactory.Create(code, "Новый анализ", DateTimeOffset.UtcNow);
        create.Should().Throw<DomainValidationException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("-7")]
    [InlineData("2345-77")]
    [InlineData("2345--7")]
    [InlineData("x-7")]
    [InlineData("２３４５-7")]
    [InlineData("2345-７")]
    [InlineData("2345\0-7")]
    public void LegacyNumericShape_ShouldStillRejectMalformedCodes(string? code)
    {
        LaboratoryTestDefinitionRules.IsWellFormedCode(code).Should().BeFalse();
    }

    [Theory]
    [InlineData("2345-7")]
    [InlineData("718-7")]
    [InlineData("7918-6")]
    [InlineData("5196-1")]
    [InlineData("13955-0")]
    [InlineData("5393-4")]
    [InlineData("101363-0")]
    [InlineData("12345-5")]
    [InlineData("1-8")]
    [InlineData(" 2345-7 ")]
    public void ValidCheckDigit_ShouldBeAcceptedByContractAndDomain(string code)
    {
        LaboratoryTestCodeRules.IsValidCode(code).Should().BeTrue();
        LaboratoryTestDefinitionRules.IsValidCode(code).Should().BeTrue();
    }

    [Theory]
    [InlineData("11491-6")]
    [InlineData("11501-6")]
    [InlineData("5895-7")]
    [InlineData("5896-5")]
    [InlineData("5897-3")]
    [InlineData("5928-1")]
    [InlineData("6028-3")]
    [InlineData("6415-5")]
    [InlineData("6580-7")]
    [InlineData("6645-7")]
    [InlineData("6646-5")]
    [InlineData("6647-3")]
    [InlineData("6648-1")]
    [InlineData("6649-9")]
    public void PublishedHistoricalException_ShouldRemainAcceptedWithoutChangingItsCode(string code)
    {
        LaboratoryTestCodeRules.IsValidCode(code).Should().BeTrue();
        var definition = LaboratoryTestDefinitionFactory.Create(code, "Исторический анализ", DateTimeOffset.UtcNow);
        definition.LoincCode.Should().Be(code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("2345-8")]
    [InlineData("718-8")]
    [InlineData("7918-5")]
    [InlineData("5196-0")]
    [InlineData("13955-9")]
    [InlineData("5393-3")]
    [InlineData("101363-1")]
    [InlineData("1-1")]
    [InlineData("2345-77")]
    [InlineData("23-45-7")]
    [InlineData("2345-")]
    [InlineData("-7")]
    [InlineData("2345-７")]
    [InlineData("２３４５-7")]
    [InlineData("2345\0-7")]
    [InlineData("2345\n-7")]
    [InlineData("LOINC:2345-7")]
    public void InvalidCode_ShouldBeRejectedByContractAndDomain(string? code)
    {
        LaboratoryTestCodeRules.IsValidCode(code).Should().BeFalse();
        LaboratoryTestDefinitionRules.IsValidCode(code).Should().BeFalse();
    }

    [Fact]
    public void LongNumericPrefix_ShouldBeValidatedWithoutIntegerParsing()
    {
        var valid = new string('9', 30) + "-0";
        LaboratoryTestCodeRules.IsValidCode(valid).Should().BeTrue();
        LaboratoryTestDefinitionRules.IsValidCode(valid).Should().BeTrue();
        LaboratoryTestCodeRules.IsValidCode(new string('9', 30) + "-1").Should().BeFalse();
        LaboratoryTestDefinitionRules.IsValidCode(new string('9', 31) + "-1").Should().BeFalse();
    }

    [Fact]
    public void UpdateWithWrongCheckDigit_ShouldPreserveTheExistingDefinition()
    {
        var now = new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
        var definition = LaboratoryTestDefinitionFactory.Create("2345-7", "Глюкоза", now);

        var update = () => LaboratoryTestDefinitionService.Update(definition, "2345-8", "Другое имя", now.AddMinutes(1));

        update.Should().Throw<DomainValidationException>();
        definition.LoincCode.Should().Be("2345-7");
        definition.DisplayName.Should().Be("Глюкоза");
        definition.UpdatedAtUtc.Should().Be(now);
    }
}
