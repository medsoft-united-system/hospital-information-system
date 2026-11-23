using FluentValidation;
using HospitalInformationSystem.Application.Models.Patients;

namespace HospitalInformationSystem.Application.Patients.Commands.CreatePatient;

public sealed class CreatePatientCommandValidator : AbstractValidator<CreatePatientCommand>
{
    public CreatePatientCommandValidator()
    {
        RuleFor(x => x.FirstName).Must(value => value is not null && value.Trim().Length is >= 1 and <= PatientFieldLimits.MaxNameLength)
            .WithMessage($"Имя: требуется от 1 до {PatientFieldLimits.MaxNameLength} символов.");
        RuleFor(x => x.LastName).Must(value => value is not null && value.Trim().Length is >= 1 and <= PatientFieldLimits.MaxNameLength)
            .WithMessage($"Фамилия: требуется от 1 до {PatientFieldLimits.MaxNameLength} символов.");
        RuleFor(x => x.MiddleName).Must(value => value is null || value.Trim().Length <= PatientFieldLimits.MaxNameLength)
            .WithMessage($"Отчество: допускается не более {PatientFieldLimits.MaxNameLength} символов.");
        RuleFor(x => x.Comment).Must(value => value is null || value.Trim().Length <= 32)
            .WithMessage("Комментарий: допускается не более 32 символов.");
        RuleFor(x => x.Sex).IsInEnum().WithMessage("Неизвестное значение пола.");
    }
}
