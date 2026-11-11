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
        var record = await context.RequireRecordAsync(request.MedicalRecordId, cancellationToken);
        var definitions = new List<LaboratoryTestDefinition>();
        foreach (var id in request.LaboratoryTestDefinitionIds)
        {
            var definition = await laboratoryTests.FindAsync(id, tracking: false, cancellationToken)
                             ?? throw new EntityNotFoundException("Лабораторный анализ не найден в справочнике.");
            if (!definition.IsActive)
                throw new DomainValidationException("Неактивный анализ нельзя назначить.");
            definitions.Add(definition);
        }
        var created = MedicalRecordService.CreateLaboratoryAnalysisRequests(record, definitions,
            request.Comment, author, context.Now);
        await context.SaveChangesAsync(record, cancellationToken);
        return created.ToDtos(record.HistoryEvents);
    }
}
