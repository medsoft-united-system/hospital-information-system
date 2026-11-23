using HospitalInformationSystem.Application.Abstractions.Repositories;
using HospitalInformationSystem.Application.Fhir.Queries;
using HospitalInformationSystem.Application.Models.Common;
using HospitalInformationSystem.Application.Models.Fhir;
using MediatR;

namespace HospitalInformationSystem.Application.Fhir.Handlers;

public sealed class GetFhirObservationQueryHandler(IFhirReadRepository repository)
    : IRequestHandler<GetFhirObservationQuery, FhirObservationReadModel>
{
    public async Task<FhirObservationReadModel> Handle(GetFhirObservationQuery request,
        CancellationToken cancellationToken)
    {
        FhirReadValidation.RequireId(request.Id);
        return await repository.GetObservationAsync(request.Id, cancellationToken)
               ?? throw new EntityNotFoundException("FHIR Observation не найден.");
    }
}
