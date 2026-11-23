using FluentValidation;
using HospitalInformationSystem.Application.Models.LaboratoryTests;
using HospitalInformationSystem.Application.Models.MedicalRecords;

namespace HospitalInformationSystem.Application.MedicalRecords.Commands.RecordLaboratoryAnalysisResult;

public sealed class RecordLaboratoryAnalysisResultCommandValidator
    : AbstractValidator<RecordLaboratoryAnalysisResultCommand>
{
    public RecordLaboratoryAnalysisResultCommandValidator()
    {
        RuleFor(x => x.RequestId).NotEmpty();
        RuleFor(x => x.PatientId).NotEmpty();
        RuleFor(x => x.LoincCode).Must(LaboratoryTestDefinitionRules.IsWellFormedCode)
            .WithMessage("Укажите код LOINC назначенного анализа.");
        RuleFor(x => x.Value).Must(LaboratoryAnalysisRules.IsValidValue)
            .WithMessage("Результат должен содержать от 1 до 2000 символов без нулевого символа.");
        RuleFor(x => x.IssuedAtUtc).Must(LaboratoryAnalysisRules.IsValidIssuedAt)
            .WithMessage("Укажите время фиксации результата с точностью не более миллисекунд.");
    }
}
