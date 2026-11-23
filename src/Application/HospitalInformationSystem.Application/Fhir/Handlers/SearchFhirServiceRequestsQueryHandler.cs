using HospitalInformationSystem.Application.Abstractions.Repositories;
using HospitalInformationSystem.Application.Fhir.Queries;
using HospitalInformationSystem.Application.Models.Fhir;
using MediatR;

namespace HospitalInformationSystem.Application.Fhir.Handlers;

public sealed class SearchFhirServiceRequestsQueryHandler(IFhirReadRepository repository)
    : IRequestHandler<SearchFhirServiceRequestsQuery, FhirReadPage<FhirServiceRequestReadModel>>
{
    public Task<FhirReadPage<FhirServiceRequestReadModel>> Handle(SearchFhirServiceRequestsQuery request,
        CancellationToken cancellationToken)
    {
        FhirReadValidation.RequirePage(request.Count, request.After);
        FhirReadValidation.RequireStatus(request.Status);
        return repository.SearchServiceRequestsAsync(request.Status, request.Count, request.After, cancellationToken);
    }
}
