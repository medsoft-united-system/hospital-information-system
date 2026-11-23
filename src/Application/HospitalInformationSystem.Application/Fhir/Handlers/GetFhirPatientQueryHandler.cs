using HospitalInformationSystem.Application.Abstractions.Repositories;
using HospitalInformationSystem.Application.Fhir.Queries;
using HospitalInformationSystem.Application.Models.Common;
using HospitalInformationSystem.Application.Models.Patients;
using MediatR;

namespace HospitalInformationSystem.Application.Fhir.Handlers;

public sealed class GetFhirPatientQueryHandler(IFhirReadRepository repository)
    : IRequestHandler<GetFhirPatientQuery, Patient>
{
    public async Task<Patient> Handle(GetFhirPatientQuery request, CancellationToken cancellationToken)
    {
        FhirReadValidation.RequireId(request.Id);
        return await repository.GetPatientAsync(request.Id, cancellationToken)
               ?? throw new EntityNotFoundException("FHIR Patient не найден.");
    }
}
