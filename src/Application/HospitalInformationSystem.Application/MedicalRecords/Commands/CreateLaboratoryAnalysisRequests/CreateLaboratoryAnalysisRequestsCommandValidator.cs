using FluentValidation;
using HospitalInformationSystem.Application.Models.MedicalRecords;

namespace HospitalInformationSystem.Application.MedicalRecords.Commands.CreateLaboratoryAnalysisRequests;

public sealed class CreateLaboratoryAnalysisRequestsCommandValidator
    : AbstractValidator<CreateLaboratoryAnalysisRequestsCommand>
{
    public CreateLaboratoryAnalysisRequestsCommandValidator()
    {
        RuleFor(x => x.MedicalRecordId).NotEmpty();
        RuleFor(x => x.LaboratoryTestDefinitionIds).NotNull()
            .Must(ids => ids is { Count: > 0 and <= LaboratoryAnalysisRules.MaxBatchSize })
            .WithMessage("Выберите от 1 до 100 анализов.")
            .Must(ids => ids is null || ids.All(id => id != Guid.Empty))
            .WithMessage("Идентификатор анализа не должен быть пустым.")
            .Must(ids => ids is null || ids.Distinct().Count() == ids.Count)
            .WithMessage("В одном выборе каждый анализ должен встречаться только один раз.");
        RuleFor(x => x.ExpectedVersions).Must((request, versions) =>
                request.LaboratoryTestDefinitionIds is { } ids && versions is not null &&
                versions.Count == ids.Count && ids.All(id => versions.TryGetValue(id, out var version) && version > 0))
            .WithMessage("Передайте текущую ненулевую версию каждого выбранного анализа без лишних идентификаторов.");
        RuleFor(x => x.Comment).Must(LaboratoryAnalysisRules.IsValidComment)
            .WithMessage("Комментарий должен содержать не более 2000 символов без нулевого символа.");
    }
}
