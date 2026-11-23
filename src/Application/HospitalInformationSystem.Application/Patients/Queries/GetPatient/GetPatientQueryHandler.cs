using HospitalInformationSystem.Abstractions.Repositories;
using HospitalInformationSystem.Models.Common;
using HospitalInformationSystem.Models.Patients;
using MediatR;

namespace HospitalInformationSystem.Application.Patients.Queries.GetPatient;

public sealed class GetPatientQueryHandler(IPatientRepository repository, IMedicalRecordPresence recordPresence)
    : IRequestHandler<GetPatientQuery, PatientDto>
{
    public async Task<PatientDto> Handle(GetPatientQuery request, CancellationToken cancellationToken)
    {
        var patient = await repository.FindAsync(request.Id, tracking: false, cancellationToken)
                      ?? throw new EntityNotFoundException("Пациент не найден.");
        return Mapping.PatientMapping.ToDto(patient) with
        {
            HasMedicalRecord = await recordPresence.ExistsForPatientAsync(patient.Id, cancellationToken)
        };
    }
}
