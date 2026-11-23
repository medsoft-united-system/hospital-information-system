using HospitalInformationSystem.Application.MedicalRecords.Services;
using HospitalInformationSystem.Application.Models.Common;
using HospitalInformationSystem.Application.Models.MedicalRecords;
using MediatR;

namespace HospitalInformationSystem.Application.MedicalRecords.Commands.RemoveMedicalRecordDiagnosis;

public sealed class RemoveMedicalRecordDiagnosisCommandHandler(MedicalRecordCommandContext context)
    : IRequestHandler<RemoveMedicalRecordDiagnosisCommand>
{
    public async Task Handle(RemoveMedicalRecordDiagnosisCommand request, CancellationToken cancellationToken)
    {
        var author = await context.RequireAuthorAsync(request.MedicalWorkerId, cancellationToken);
        var record = await context.RequireRecordAsync(request.MedicalRecordId, cancellationToken);
        MedicalRecordService.RemoveDiagnosis(record, request.MedicalRecordDiagnosisId, author, context.Now);
        await context.SaveChangesAsync(record, cancellationToken);
    }
}
