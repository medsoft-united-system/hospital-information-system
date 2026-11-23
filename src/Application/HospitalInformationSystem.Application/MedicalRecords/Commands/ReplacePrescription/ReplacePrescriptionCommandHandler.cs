using HospitalInformationSystem.Application.MedicalRecords.Services;
using HospitalInformationSystem.Application.Models.Common;
using HospitalInformationSystem.Application.Models.MedicalRecords;
using MediatR;

namespace HospitalInformationSystem.Application.MedicalRecords.Commands.ReplacePrescription;

public sealed class ReplacePrescriptionCommandHandler(MedicalRecordCommandContext context)
    : IRequestHandler<ReplacePrescriptionCommand, PrescriptionDto>
{
    public async Task<PrescriptionDto> Handle(ReplacePrescriptionCommand request, CancellationToken cancellationToken)
    {
        var author = await context.RequireAuthorAsync(request.MedicalWorkerId, cancellationToken);
        var record = await context.RequireRecordAsync(request.MedicalRecordId, cancellationToken);
        var result =
            MedicalRecordService.ReplacePrescription(record, request.PrescriptionId, request.Text, author, context.Now);
        await context.SaveChangesAsync(record, cancellationToken);
        return Mapping.MedicalRecordMapping.ToDto(result);
    }
}
