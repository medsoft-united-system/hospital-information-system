using HospitalInformationSystem.Application.Abstractions.Repositories;
using HospitalInformationSystem.Application.Models.Common;
using HospitalInformationSystem.Application.Models.MedicalRecords;
using MediatR;

namespace HospitalInformationSystem.Application.MedicalRecords.Queries.GetPatientMedicalRecord;

public sealed class GetPatientMedicalRecordQueryHandler(IMedicalRecordRepository repository)
    : IRequestHandler<GetPatientMedicalRecordQuery, MedicalRecordDto>
{
    public async Task<MedicalRecordDto> Handle(GetPatientMedicalRecordQuery request,
        CancellationToken cancellationToken)
    {
        var record = await repository.FindByPatientAsync(request.PatientId, cancellationToken)
                     ?? throw new EntityNotFoundException("ЭМК пациента не найдена.");
        return Mapping.MedicalRecordMapping.ToDto(record);
    }
}
