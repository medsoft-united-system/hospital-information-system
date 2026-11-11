using System.Globalization;
using Hl7.Fhir.Model;
using HospitalInformationSystem.Application.Fhir;
using DomainPatient = HospitalInformationSystem.Domain.Patients.Patient;
using DomainSex = HospitalInformationSystem.Domain.Patients.AdministrativeSex;
using DomainWorker = HospitalInformationSystem.Domain.MedicalWorkers.MedicalWorker;
using DomainRequestStatus = HospitalInformationSystem.Domain.MedicalRecords.LaboratoryAnalysisRequestStatus;
using FhirCapabilityStatement = Hl7.Fhir.Model.CapabilityStatement;
using FhirOperationOutcome = Hl7.Fhir.Model.OperationOutcome;

namespace HospitalInformationSystem.Api.Fhir;

public static class FhirResourceMapper
{
    public const string LoincSystem = "http://loinc.org";
    public const string ErrorCodeSystem = "urn:medsoft:lab3:error";

    public static Patient ToPatient(DomainPatient patient) => new()
    {
        Id = Id(patient.Id), Active = true,
        Name = [Name(patient.LastName, patient.FirstName, patient.MiddleName)],
        BirthDate = patient.BirthDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        Gender = patient.Sex switch
        {
            DomainSex.Male => AdministrativeGender.Male,
            DomainSex.Female => AdministrativeGender.Female,
            _ => AdministrativeGender.Unknown
        }
    };

    public static Practitioner ToPractitioner(DomainWorker worker) => new()
    {
        Id = Id(worker.Id), Active = !worker.IsDeleted,
        Name = [Name(worker.LastName, worker.FirstName)],
        Qualification = string.IsNullOrWhiteSpace(worker.Position) ? [] :
            [new Practitioner.QualificationComponent { Code = new CodeableConcept { Text = worker.Position } }]
    };

    public static ServiceRequest ToServiceRequest(FhirServiceRequestReadModel request) => new()
    {
        Id = Id(request.Id),
        Status = request.Status switch
        {
            DomainRequestStatus.Active => RequestStatus.Active,
            DomainRequestStatus.Completed => RequestStatus.Completed,
            _ => throw new InvalidOperationException("Неизвестное состояние лабораторного назначения.")
        },
        Intent = RequestIntent.Order,
        Code = new CodeableReference { Concept = Loinc(request.LoincCode, request.DisplayName) },
        Subject = Reference("Patient", request.PatientId),
        Requester = Reference("Practitioner", request.RequestedByMedicalWorkerId),
        AuthoredOn = FhirTimestamp.Format(request.AuthoredAtUtc),
        Note = string.IsNullOrWhiteSpace(request.Comment) ? [] : [new Annotation { Text = request.Comment }]
    };

    public static Observation ToObservation(FhirObservationReadModel result) => new()
    {
        Id = Id(result.Id), Status = ObservationStatus.Final,
        Code = Loinc(result.LoincCode, result.DisplayName),
        Subject = Reference("Patient", result.PatientId),
        BasedOn = [Reference("ServiceRequest", result.LaboratoryAnalysisRequestId)],
        IssuedElement = new Instant { JsonValue = FhirTimestamp.Format(result.IssuedAtUtc) },
        Value = new FhirString(result.Value)
    };

    public static FhirCapabilityStatement CapabilityStatement() => new()
    {
        Id = "medsoft-fhir-r5", Name = "MedSoftFhirR5", Title = "MedSoft HIS FHIR R5",
        Version = "lab3-l6", Status = PublicationStatus.Active,
        Date = "2026-11-09T00:00:00Z", Kind = CapabilityStatementKind.Capability,
        Software = new FhirCapabilityStatement.SoftwareComponent { Name = "MedSoft HIS", Version = "lab3-l6" },
        FhirVersion = FHIRVersion.N5_0_0,
        Format = [FhirResponseWriter.ContentType],
        Rest = [new FhirCapabilityStatement.RestComponent
        {
            Mode = FhirCapabilityStatement.RestfulCapabilityMode.Server,
            Documentation = "Medical resources require the dedicated LIS service identity. Observation create registers one immutable result per ServiceRequest.",
            Resource =
            [
                ResourceCapability("Patient"),
                ResourceCapability("Practitioner"),
                ResourceCapability("ServiceRequest", new FhirCapabilityStatement.SearchParamComponent
                    { Name = "status", Type = SearchParamType.Token, Documentation = "active or completed; omit to read both." }),
                ResourceCapability("Observation", new FhirCapabilityStatement.SearchParamComponent
                    { Name = "based-on", Type = SearchParamType.Reference, Documentation = "Required relative ServiceRequest/{uuid} reference." }, create: true)
            ]
        }]
    };

    public static Bundle SearchBundle(string resourceType, IEnumerable<Resource> resources,
        string selfUrl, string? nextUrl)
    {
        var self = Endpoint(selfUrl, resourceType);
        if (nextUrl is not null)
        {
            var next = Endpoint(nextUrl, resourceType);
            if (self.Scheme != next.Scheme || self.Authority != next.Authority || self.AbsolutePath != next.AbsolutePath)
                throw new InvalidOperationException("Ссылка следующей страницы должна принадлежать тому же поиску.");
        }
        var items = resources.ToArray();
        if (items.Any(resource => resource.TypeName != resourceType ||
                                  !Guid.TryParseExact(resource.Id, "D", out var id) || id == Guid.Empty ||
                                  resource.Id != Id(id)))
            throw new InvalidOperationException("Поисковый Bundle содержит неверный тип или идентификатор ресурса.");
        var resourceEndpoint = self.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/";
        var bundle = new Bundle
        {
            Type = Bundle.BundleType.Searchset,
            Link = [new Bundle.LinkComponent { Relation = "self", Url = selfUrl }],
            Entry = items.Select(resource => new Bundle.EntryComponent
            {
                FullUrl = resourceEndpoint + resource.Id, Resource = resource,
                Search = new Bundle.SearchComponent { Mode = Bundle.SearchEntryMode.Match }
            }).ToList()
        };
        if (nextUrl is not null) bundle.Link.Add(new Bundle.LinkComponent { Relation = "next", Url = nextUrl });
        if (items.Length == 0) bundle.Total = 0;
        return bundle;
    }

    public static FhirOperationOutcome OperationOutcome(int status, string machineCode, string message,
        string? traceId = null)
    {
        var outcome = new FhirOperationOutcome
        {
            Issue = [new FhirOperationOutcome.IssueComponent
            {
                Severity = FhirOperationOutcome.IssueSeverity.Error,
                Code = status switch
                {
                    401 => FhirOperationOutcome.IssueType.Login,
                    403 => FhirOperationOutcome.IssueType.Forbidden,
                    404 => FhirOperationOutcome.IssueType.NotFound,
                    405 or 406 or 415 => FhirOperationOutcome.IssueType.NotSupported,
                    409 => FhirOperationOutcome.IssueType.Conflict,
                    413 => FhirOperationOutcome.IssueType.TooCostly,
                    429 => FhirOperationOutcome.IssueType.Throttled,
                    >= 500 => FhirOperationOutcome.IssueType.Exception,
                    _ => FhirOperationOutcome.IssueType.Invalid
                },
                Details = new CodeableConcept { Coding = [new Coding(ErrorCodeSystem, machineCode)] },
                Diagnostics = message
            }]
        };
        if (!string.IsNullOrWhiteSpace(traceId))
            outcome.Extension.Add(new Extension("urn:medsoft:lab3:trace-id", new FhirString(traceId)));
        return outcome;
    }

    private static FhirCapabilityStatement.ResourceComponent ResourceCapability(string type,
        FhirCapabilityStatement.SearchParamComponent? search = null, bool create = false)
    {
        var interactions = new List<FhirCapabilityStatement.ResourceInteractionComponent>
            { new() { Code = FhirCapabilityStatement.TypeRestfulInteraction.Read } };
        if (search is not null)
            interactions.Add(new() { Code = FhirCapabilityStatement.TypeRestfulInteraction.SearchType });
        if (create) interactions.Add(new() { Code = FhirCapabilityStatement.TypeRestfulInteraction.Create });
        return new FhirCapabilityStatement.ResourceComponent
        {
            Type = type, Interaction = interactions, SearchParam = search is null ? [] : [search],
            UpdateCreate = create ? false : null,
            ConditionalCreate = create ? false : null
        };
    }

    private static HumanName Name(string family, string first, string? middle = null) => new()
    {
        Use = HumanName.NameUse.Official, Family = family,
        Given = string.IsNullOrWhiteSpace(middle) ? [first] : [first, middle]
    };

    private static CodeableConcept Loinc(string code, string display) => new()
    {
        Coding = [new Coding(LoincSystem, code, display)]
    };

    private static ResourceReference Reference(string type, Guid id) => new($"{type}/{Id(id)}");
    private static string Id(Guid id) => id.ToString("D");

    private static Uri Endpoint(string url, string resourceType)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            !uri.AbsolutePath.EndsWith("/" + resourceType, StringComparison.Ordinal))
            throw new InvalidOperationException("Некорректный адрес FHIR-поиска.");
        return uri;
    }
}
