using HospitalInformationSystem.Abstractions.Repositories;
using HospitalInformationSystem.Models.Common;
using HospitalInformationSystem.Models.MedicalRecords;
using MediatR;

namespace HospitalInformationSystem.Application.MedicalRecords.Queries.GetMedicalRecord;

public sealed class GetMedicalRecordQueryHandler(IMedicalRecordRepository repository)
    : IRequestHandler<GetMedicalRecordQuery, MedicalRecordDto>
{
    public async Task<MedicalRecordDto> Handle(GetMedicalRecordQuery request, CancellationToken cancellationToken)
    {
        var record = await repository.FindAsync(request.Id, tracking: false, cancellationToken)
                     ?? throw new EntityNotFoundException("ЭМК не найдена.");
        return Mapping.MedicalRecordMapping.ToDto(record);
    }
}
