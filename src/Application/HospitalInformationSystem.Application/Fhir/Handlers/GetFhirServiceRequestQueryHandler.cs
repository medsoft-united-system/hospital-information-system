using HospitalInformationSystem.Application.Abstractions.Repositories;
using HospitalInformationSystem.Application.Fhir.Queries;
using HospitalInformationSystem.Application.Models.Common;
using HospitalInformationSystem.Application.Models.Fhir;
using MediatR;

namespace HospitalInformationSystem.Application.Fhir.Handlers;

public sealed class GetFhirServiceRequestQueryHandler(IFhirReadRepository repository)
    : IRequestHandler<GetFhirServiceRequestQuery, FhirServiceRequestReadModel>
{
    public async Task<FhirServiceRequestReadModel> Handle(GetFhirServiceRequestQuery request,
        CancellationToken cancellationToken)
    {
        FhirReadValidation.RequireId(request.Id);
        return await repository.GetServiceRequestAsync(request.Id, cancellationToken)
               ?? throw new EntityNotFoundException("FHIR ServiceRequest не найден.");
    }
}
