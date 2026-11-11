using HospitalInformationSystem.Application.Abstractions;
using HospitalInformationSystem.Application.Common;
using HospitalInformationSystem.Application.MedicalRecords.Mapping;
using HospitalInformationSystem.Contracts.MedicalRecords;
using HospitalInformationSystem.Domain.Common;
using HospitalInformationSystem.Domain.LaboratoryTests;
using HospitalInformationSystem.Domain.MedicalRecords.Services;
using HospitalInformationSystem.Domain.MedicalWorkers;
using MediatR;

namespace HospitalInformationSystem.Application.MedicalRecords.Commands.CreateLaboratoryAnalysisRequests;

public sealed class CreateLaboratoryAnalysisRequestsCommandHandler(MedicalRecordCommandContext context,
    ILaboratoryTestRepository laboratoryTests, ICurrentMedicalWorker currentWorker)
    : IRequestHandler<CreateLaboratoryAnalysisRequestsCommand, IReadOnlyList<LaboratoryAnalysisRequestDto>>
{
    public async Task<IReadOnlyList<LaboratoryAnalysisRequestDto>> Handle(
        CreateLaboratoryAnalysisRequestsCommand request, CancellationToken cancellationToken)
    {
        var author = await context.RequireAuthorAsync(currentWorker.RequireId(), cancellationToken);
        if (author.Role != MedicalWorkerRole.Doctor)
            throw new ClinicalAccessDeniedException();
        if (request.LaboratoryTestDefinitionIds is not { } ids || request.ExpectedVersions is not { } versions ||
            versions.Count != ids.Count || ids.Any(id => !versions.TryGetValue(id, out var version) || version == 0))
            throw new DomainValidationException("Передайте текущую ненулевую версию каждого выбранного анализа без лишних идентификаторов.");
        var record = await context.RequireRecordAsync(request.MedicalRecordId, cancellationToken);
        var definitions = new List<LaboratoryTestDefinition>();
        foreach (var id in request.LaboratoryTestDefinitionIds)
        {
            var definition = await laboratoryTests.FindAsync(id, tracking: false, cancellationToken)
                             ?? throw new EntityNotFoundException("Лабораторный анализ не найден в справочнике.");
            if (definition.Version != versions[id])
                throw new BusinessConflictException("Выбранный анализ уже изменён. Обновите справочник и повторите назначение.");
            if (!definition.IsActive)
                throw new DomainValidationException("Неактивный анализ нельзя назначить.");
            definitions.Add(definition);
        }
        var created = MedicalRecordService.CreateLaboratoryAnalysisRequests(record, definitions,
            request.Comment, author, context.Now);
        await context.SaveChangesAsync(record, cancellationToken);
        return created.Select(x => x.ToDto()).ToArray();
    }
}
