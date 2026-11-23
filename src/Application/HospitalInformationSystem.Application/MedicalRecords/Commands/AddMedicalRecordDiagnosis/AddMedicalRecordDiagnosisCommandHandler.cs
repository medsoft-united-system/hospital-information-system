using HospitalInformationSystem.Application.Abstractions.Repositories;
using HospitalInformationSystem.Application.MedicalRecords.Services;
using HospitalInformationSystem.Application.Models.Common;
using HospitalInformationSystem.Application.Models.MedicalRecords;
using MediatR;

namespace HospitalInformationSystem.Application.MedicalRecords.Commands.AddMedicalRecordDiagnosis;

public sealed class AddMedicalRecordDiagnosisCommandHandler(
    MedicalRecordCommandContext context,
    IDiagnosisRepository diagnoses
) : IRequestHandler<AddMedicalRecordDiagnosisCommand, MedicalRecordDiagnosisDto>
{
    public async Task<MedicalRecordDiagnosisDto> Handle(AddMedicalRecordDiagnosisCommand request,
        CancellationToken cancellationToken)
    {
        var author = await context.RequireAuthorAsync(request.MedicalWorkerId, cancellationToken);
        var record = await context.RequireRecordAsync(request.MedicalRecordId, cancellationToken);
        var definition = await diagnoses.FindAsync(request.DiagnosisId, tracking: false, cancellationToken)
                         ?? throw new EntityNotFoundException("Диагноз справочника не найден.");
        var result = MedicalRecordService.AddDiagnosis(record, definition.Id, definition.Code, definition.Name, author,
            context.Now);
        await context.SaveChangesAsync(record, cancellationToken);
        return Mapping.MedicalRecordMapping.ToDto(result);
    }
}
