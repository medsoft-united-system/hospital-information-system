using HospitalInformationSystem.Application.Abstractions;
using HospitalInformationSystem.Application.Common;
using HospitalInformationSystem.Application.MedicalRecords.Mapping;
using HospitalInformationSystem.Contracts.MedicalRecords;
using HospitalInformationSystem.Domain.MedicalRecords.Services;
using MediatR;

namespace HospitalInformationSystem.Application.MedicalRecords.Commands.RecordLaboratoryAnalysisResult;

public sealed class RecordLaboratoryAnalysisResultCommandHandler(MedicalRecordCommandContext context,
    IMedicalRecordRepository records, ICurrentServiceAccount currentService)
    : IRequestHandler<RecordLaboratoryAnalysisResultCommand, LaboratoryAnalysisResultDto>
{
    public async Task<LaboratoryAnalysisResultDto> Handle(RecordLaboratoryAnalysisResultCommand request,
        CancellationToken cancellationToken)
    {
        var author = currentService.RequireIdentity();
        var recordId = await records.FindRecordIdByLaboratoryAnalysisRequestAsync(request.RequestId, cancellationToken)
                       ?? throw new EntityNotFoundException("Назначение лабораторного анализа не найдено.");
        var record = await context.RequireRecordAsync(recordId, cancellationToken);
        var result = MedicalRecordService.RecordLaboratoryAnalysisResult(record, request.RequestId,
            request.PatientId, request.LoincCode, request.Value, request.IssuedAtUtc, author.Id, author.Name,
            context.Now);
        await context.SaveChangesAsync(record, cancellationToken);
        return result.ToDto();
    }
}
