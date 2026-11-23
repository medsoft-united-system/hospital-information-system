using FluentValidation;
using HospitalInformationSystem.Models.Diagnoses;

namespace HospitalInformationSystem.Application.Diagnoses.Commands.UpdateDiagnosis;

public sealed class UpdateDiagnosisCommandValidator : AbstractValidator<UpdateDiagnosisCommand>
{
    public UpdateDiagnosisCommandValidator()
    {
        RuleFor(x => x.Code).Must(value => value is not null &&
                value.Trim().Length is >= DiagnosisLimits.MinCodeLength and <= DiagnosisLimits.MaxCodeLength)
            .WithMessage($"Код диагноза должен содержать от {DiagnosisLimits.MinCodeLength} до {DiagnosisLimits.MaxCodeLength} символов.");
        RuleFor(x => x.Name).Must(value => value is not null &&
                value.Trim().Length is >= DiagnosisLimits.MinNameLength and <= DiagnosisLimits.MaxNameLength)
            .WithMessage($"Название диагноза должно содержать от {DiagnosisLimits.MinNameLength} до {DiagnosisLimits.MaxNameLength} символов.");
    }
}

