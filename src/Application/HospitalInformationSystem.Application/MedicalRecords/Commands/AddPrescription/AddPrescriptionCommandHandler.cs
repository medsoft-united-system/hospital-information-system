using HospitalInformationSystem.Application.MedicalRecords.Services;
using HospitalInformationSystem.Models.Common;
using HospitalInformationSystem.Models.MedicalRecords;
using MediatR;

namespace HospitalInformationSystem.Application.MedicalRecords.Commands.AddPrescription;

public sealed class AddPrescriptionCommandHandler(MedicalRecordCommandContext context)
    : IRequestHandler<AddPrescriptionCommand, PrescriptionDto>
{
    public async Task<PrescriptionDto> Handle(AddPrescriptionCommand request, CancellationToken cancellationToken)
    {
        var author = await context.RequireAuthorAsync(request.MedicalWorkerId, cancellationToken);
        var record = await context.RequireRecordAsync(request.MedicalRecordId, cancellationToken);
        var result = MedicalRecordService.AddPrescription(record, request.Text, author, context.Now);
        await context.SaveChangesAsync(record, cancellationToken);
        return Mapping.MedicalRecordMapping.ToDto(result);
    }
}
