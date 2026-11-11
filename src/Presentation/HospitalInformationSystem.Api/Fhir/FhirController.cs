using HospitalInformationSystem.Application.Fhir;
using HospitalInformationSystem.Application.MedicalRecords.Commands.RecordLaboratoryAnalysisResult;
using HospitalInformationSystem.Contracts.ServiceAuth;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HospitalInformationSystem.Api.Fhir;

[ApiController]
[Route("fhir")]
[Authorize(AuthenticationSchemes = ServiceAuthenticationDefaults.Scheme,
    Policy = ServiceAuthenticationDefaults.Policies.FhirRead)]
public sealed class FhirController(ISender sender, FhirResponseWriter writer, FhirSearchParser parser,
    FhirCursorProtector cursors, FhirEndpointOptions endpoint, FhirObservationReader observations) : ControllerBase
{
    [HttpPost("Observation")]
    [Authorize(AuthenticationSchemes = ServiceAuthenticationDefaults.Scheme,
        Policy = ServiceAuthenticationDefaults.Policies.ObservationCreate)]
    [RequestSizeLimit(64 * 1024)]
    public async Task<IActionResult> CreateObservationAsync(CancellationToken cancellationToken)
    {
        FhirSearchParser.RejectQuery(Request.Query);
        var input = await observations.ReadAsync(Request, cancellationToken);
        var order = await sender.Send(new GetFhirServiceRequestQuery(input.RequestId), cancellationToken);
        var result = await sender.Send(new RecordLaboratoryAnalysisResultCommand(input.RequestId, input.PatientId,
            input.LoincCode, input.Value, input.IssuedAtUtc), cancellationToken);
        var observation = FhirResourceMapper.ToObservation(new FhirObservationReadModel(result.Id,
            result.LaboratoryAnalysisRequestId, result.PatientId, order.LoincCode, order.DisplayName, result.Value,
            result.IssuedAtUtc));
        Response.Headers.Location = endpoint.PublicBaseUrl + "/Observation/" + result.Id.ToString("D");
        await writer.WriteAsync(HttpContext, observation, StatusCodes.Status201Created, cancellationToken);
        return new EmptyResult();
    }

    [HttpGet("metadata")]
    [AllowAnonymous]
    public async Task<IActionResult> MetadataAsync(CancellationToken cancellationToken)
    {
        FhirSearchParser.RejectQuery(Request.Query);
        await writer.WriteAsync(HttpContext, FhirResourceMapper.CapabilityStatement(), 200, cancellationToken);
        return new EmptyResult();
    }

    [HttpGet("Patient/{id}")]
    public async Task<IActionResult> GetPatientAsync(string id, CancellationToken cancellationToken)
    {
        FhirSearchParser.RejectQuery(Request.Query);
        var patient = await sender.Send(new GetFhirPatientQuery(FhirSearchParser.ParseResourceId(id)), cancellationToken);
        await writer.WriteAsync(HttpContext, FhirResourceMapper.ToPatient(patient), 200, cancellationToken);
        return new EmptyResult();
    }

    [HttpGet("Practitioner/{id}")]
    public async Task<IActionResult> GetPractitionerAsync(string id, CancellationToken cancellationToken)
    {
        FhirSearchParser.RejectQuery(Request.Query);
        var practitioner = await sender.Send(new GetFhirPractitionerQuery(FhirSearchParser.ParseResourceId(id)), cancellationToken);
        await writer.WriteAsync(HttpContext, FhirResourceMapper.ToPractitioner(practitioner), 200, cancellationToken);
        return new EmptyResult();
    }

    [HttpGet("ServiceRequest/{id}")]
    public async Task<IActionResult> GetServiceRequestAsync(string id, CancellationToken cancellationToken)
    {
        FhirSearchParser.RejectQuery(Request.Query);
        var request = await sender.Send(new GetFhirServiceRequestQuery(FhirSearchParser.ParseResourceId(id)), cancellationToken);
        await writer.WriteAsync(HttpContext, FhirResourceMapper.ToServiceRequest(request), 200, cancellationToken);
        return new EmptyResult();
    }

    [HttpGet("Observation/{id}")]
    public async Task<IActionResult> GetObservationAsync(string id, CancellationToken cancellationToken)
    {
        FhirSearchParser.RejectQuery(Request.Query);
        var observation = await sender.Send(new GetFhirObservationQuery(FhirSearchParser.ParseResourceId(id)), cancellationToken);
        await writer.WriteAsync(HttpContext, FhirResourceMapper.ToObservation(observation), 200, cancellationToken);
        return new EmptyResult();
    }

    [HttpGet("ServiceRequest")]
    public async Task<IActionResult> SearchServiceRequestsAsync(CancellationToken cancellationToken)
    {
        var search = parser.ParseServiceRequests(Request.Query);
        var page = await sender.Send(new SearchFhirServiceRequestsQuery(search.Status, search.Count, search.After),
            cancellationToken);
        var next = page.Next is null ? null : endpoint.SearchUrl(search, cursors.Protect(search, page.Next));
        var bundle = FhirResourceMapper.SearchBundle(search.ResourceType,
            page.Items.Select(FhirResourceMapper.ToServiceRequest), endpoint.SearchUrl(search, search.Cursor), next,
            isFirstPage: search.Cursor is null);
        await writer.WriteAsync(HttpContext, bundle, 200, cancellationToken);
        return new EmptyResult();
    }

    [HttpGet("Observation")]
    public async Task<IActionResult> SearchObservationsAsync(CancellationToken cancellationToken)
    {
        var search = parser.ParseObservations(Request.Query);
        var page = await sender.Send(new SearchFhirObservationsQuery(search.BasedOn!.Value, search.Count, search.After),
            cancellationToken);
        var next = page.Next is null ? null : endpoint.SearchUrl(search, cursors.Protect(search, page.Next));
        var bundle = FhirResourceMapper.SearchBundle(search.ResourceType,
            page.Items.Select(FhirResourceMapper.ToObservation), endpoint.SearchUrl(search, search.Cursor), next,
            isFirstPage: search.Cursor is null);
        await writer.WriteAsync(HttpContext, bundle, 200, cancellationToken);
        return new EmptyResult();
    }
}
