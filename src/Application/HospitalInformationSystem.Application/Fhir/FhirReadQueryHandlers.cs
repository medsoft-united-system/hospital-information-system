using HospitalInformationSystem.Application.Abstractions;
using HospitalInformationSystem.Application.Common;
using HospitalInformationSystem.Domain.MedicalWorkers;
using HospitalInformationSystem.Domain.Patients;
using MediatR;

namespace HospitalInformationSystem.Application.Fhir;

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
