using System;
using FluentAssertions;
using HospitalInformationSystem.Application.Patients.Factories;
using HospitalInformationSystem.Application.Patients.Services;
using HospitalInformationSystem.Models.Common;
using HospitalInformationSystem.Models.Patients;
using Xunit;

namespace HospitalInformationSystem.UnitTests;

public sealed class PatientTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.FromHours(3));

    [Fact]
    public void Create_OptionalText_ShouldNormalizeTextAndPreserveBirthDate()
    {
        var patient = PatientFactory.Create(" Елена ", " Соколова ", "  ", new DateOnly(1985, 3, 12),
            AdministrativeSex.Female, " Аллергия ", Now);

        patient.FirstName.Should().Be("Елена");
        patient.LastName.Should().Be("Соколова");
        patient.MiddleName.Should().BeNull();
        patient.Comment.Should().Be("Аллергия");
        patient.BirthDate.Should().Be(new DateOnly(1985, 3, 12));
        patient.CreatedAtUtc.Offset.Should().Be(TimeSpan.Zero);
        patient.Id.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("12345678901234567890123456789012345678901234567890123456789012345")]
    public void Create_InvalidName_ShouldRejectInput(string? name)
    {
        var act = () => PatientFactory.Create(name!, "Соколова", null, new DateOnly(1985, 3, 12),
            AdministrativeSex.Female, null, Now);
        act.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void Update_InvalidFields_ShouldPreservePatient()
    {
        var patient = PatientFactory.Create("Елена", "Соколова", null, new DateOnly(1985, 3, 12),
            AdministrativeSex.Female, null, Now);
        var act = () => PatientService.Update(patient, "Мария", "Петрова", "Ивановна", new DateOnly(1990, 1, 1),
            AdministrativeSex.Unknown, new string('x', 33), Now.AddDays(1));

        act.Should().Throw<DomainValidationException>();
        patient.FirstName.Should().Be("Елена");
        patient.BirthDate.Should().Be(new DateOnly(1985, 3, 12));
        patient.UpdatedAtUtc.Should().Be(Now);
    }

    [Fact]
    public void Create_UnknownSexValue_ShouldRejectInput()
    {
        var act = () => PatientFactory.Create("Елена", "Соколова", null, new DateOnly(1985, 3, 12),
            (AdministrativeSex)123, null, Now);
        act.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void Create_FutureBirthDate_ShouldRejectInput()
    {
        var act = () => PatientFactory.Create("Елена", "Соколова", null,
            DateOnly.FromDateTime(Now.UtcDateTime).AddDays(1), AdministrativeSex.Female, null, Now);

        act.Should().Throw<DomainValidationException>().WithMessage("*Дата рождения*");
    }

    [Fact]
    public void Update_FutureBirthDate_ShouldPreservePatient()
    {
        var patient = PatientFactory.Create("Елена", "Соколова", null, new DateOnly(1985, 3, 12),
            AdministrativeSex.Female, null, Now);
        var act = () => PatientService.Update(patient, "Мария", "Петрова", "Ивановна",
            DateOnly.FromDateTime(Now.UtcDateTime).AddDays(1), AdministrativeSex.Unknown, "Обновлено", Now);

        act.Should().Throw<DomainValidationException>();
        patient.FirstName.Should().Be("Елена");
        patient.LastName.Should().Be("Соколова");
        patient.MiddleName.Should().BeNull();
        patient.BirthDate.Should().Be(new DateOnly(1985, 3, 12));
        patient.Sex.Should().Be(AdministrativeSex.Female);
        patient.Comment.Should().BeNull();
        patient.UpdatedAtUtc.Should().Be(Now);
    }

    [Fact]
    public void Create_UtcTodayBirthDate_ShouldAcceptInput()
    {
        var now = new DateTimeOffset(2026, 10, 5, 1, 0, 0, TimeSpan.FromHours(3));
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        var patient = PatientFactory.Create("Елена", "Соколова", null, today,
            AdministrativeSex.Female, null, now);

        patient.BirthDate.Should().Be(today);
    }

    [Fact]
    public void Create_TomorrowInUtcButTodayInOffset_ShouldRejectInput()
    {
        var now = new DateTimeOffset(2026, 10, 5, 1, 0, 0, TimeSpan.FromHours(3));
        var act = () => PatientFactory.Create("Елена", "Соколова", null, new DateOnly(2026, 10, 5),
            AdministrativeSex.Female, null, now);

        act.Should().Throw<DomainValidationException>();
    }
}
