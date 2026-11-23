using HospitalInformationSystem.Application.Abstractions.Repositories;
using HospitalInformationSystem.Application.Fhir.Queries;
using HospitalInformationSystem.Application.Models.Common;
using HospitalInformationSystem.Application.Models.MedicalWorkers;
using MediatR;

namespace HospitalInformationSystem.Application.Fhir.Handlers;

public sealed class GetFhirPractitionerQueryHandler(IFhirReadRepository repository)
    : IRequestHandler<GetFhirPractitionerQuery, MedicalWorker>
{
    public async Task<MedicalWorker> Handle(GetFhirPractitionerQuery request, CancellationToken cancellationToken)
    {
        FhirReadValidation.RequireId(request.Id);
        return await repository.GetPractitionerAsync(request.Id, cancellationToken)
               ?? throw new EntityNotFoundException("FHIR Practitioner не найден.");
    }
}
