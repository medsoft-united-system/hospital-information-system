using HospitalInformationSystem.Application.Abstractions.Repositories;
using HospitalInformationSystem.Application.Fhir.Queries;
using HospitalInformationSystem.Application.Models.Fhir;
using MediatR;

namespace HospitalInformationSystem.Application.Fhir.Handlers;

public sealed class SearchFhirObservationsQueryHandler(IFhirReadRepository repository)
    : IRequestHandler<SearchFhirObservationsQuery, FhirReadPage<FhirObservationReadModel>>
{
    public Task<FhirReadPage<FhirObservationReadModel>> Handle(SearchFhirObservationsQuery request,
        CancellationToken cancellationToken)
    {
        FhirReadValidation.RequireId(request.BasedOn);
        FhirReadValidation.RequirePage(request.Count, request.After);
        return repository.SearchObservationsAsync(request.BasedOn, request.Count, request.After, cancellationToken);
    }
}
