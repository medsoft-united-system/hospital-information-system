using System;
using FluentAssertions;
using HospitalInformationSystem.Application.Diagnoses.Factories;
using HospitalInformationSystem.Application.Diagnoses.Services;
using HospitalInformationSystem.Application.Models.Common;
using HospitalInformationSystem.Application.Models.Diagnoses;
using Xunit;

namespace HospitalInformationSystem.UnitTests;

public sealed class DiagnosisDefinitionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(null, "Холера")]
    [InlineData("AB", "Холера")]
    [InlineData("123456789012345678901234567890123", "Холера")]
    [InlineData("A00.00", null)]
    [InlineData("A00.00", "АБ")]
    public void Create_InvalidCodeOrName_ShouldRejectInput(string? code, string? name)
    {
        var act = () => DiagnosisDefinitionFactory.Create(code!, name!, Now);
        act.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void Create_NameAboveMaximum_ShouldRejectInput()
    {
        var act = () => DiagnosisDefinitionFactory.Create("I10", new string('Н', 257), Now);

        act.Should().Throw<DomainValidationException>();
    }

    [Theory]
    [InlineData("I10", 3)]
    [InlineData("I48", 256)]
    [InlineData("J45.9", 25)]
    [InlineData("I10.00", 24)]
    [InlineData("local-code/value", 32)]
    [InlineData("12345678901234567890123456789012", 256)]
    public void Create_ValidLimits_ShouldPreserveExactCodeAndTrimOuterWhitespace(string code, int nameLength)
    {
        var name = new string('Н', nameLength);

        var diagnosis = DiagnosisDefinitionFactory.Create($" {code} ", $" {name} ", Now);

        diagnosis.Code.Should().Be(code);
        diagnosis.Name.Should().Be(name);
    }

    [Fact]
    public void Update_ValidFields_ShouldNormalizeTextAndPreserveIdentityAndCreationTime()
    {
        var diagnosis = DiagnosisDefinitionFactory.Create(" A00.00 ", " Холера ", Now);
        var id = diagnosis.Id;
        DiagnosisDefinitionService.Update(diagnosis, " B01.00 ", " Ветряная оспа ", Now.AddHours(1));

        diagnosis.Code.Should().Be("B01.00");
        diagnosis.Name.Should().Be("Ветряная оспа");
        diagnosis.Id.Should().Be(id);
        diagnosis.CreatedAtUtc.Should().Be(Now);
        diagnosis.UpdatedAtUtc.Should().Be(Now.AddHours(1));
    }

    [Fact]
    public void Update_InvalidFields_ShouldPreserveCodeAndTimestamp()
    {
        var diagnosis = DiagnosisDefinitionFactory.Create("A00.00", "Холера", Now);
        var act = () => DiagnosisDefinitionService.Update(diagnosis, "B01.00", " ", Now.AddHours(1));

        act.Should().Throw<DomainValidationException>();
        diagnosis.Code.Should().Be("A00.00");
        diagnosis.UpdatedAtUtc.Should().Be(Now);
    }
}
