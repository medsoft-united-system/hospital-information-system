using System.Globalization;
using Microsoft.AspNetCore.Http;

namespace HospitalInformationSystem.Api.Fhir;

public sealed class FhirEndpointOptions
{
    public const string ConfigurationKey = "Fhir:PublicBaseUrl";
    public const string DefaultPublicBaseUrl = "https://localhost:5201/fhir";

    public FhirEndpointOptions(IConfiguration configuration)
        : this(configuration[ConfigurationKey] ?? DefaultPublicBaseUrl) { }

    public FhirEndpointOptions(string publicBaseUrl)
    {
        var value = publicBaseUrl.TrimEnd('/');
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            value.Contains('\\') || value.Contains('%') || value.Any(char.IsWhiteSpace) ||
            uri.Host.Contains('*') || !uri.AbsolutePath.EndsWith("/fhir", StringComparison.Ordinal) ||
            value.Contains("/../", StringComparison.Ordinal) || value.Contains("/./", StringComparison.Ordinal) ||
            uri.AbsolutePath.Contains("//", StringComparison.Ordinal))
            throw new InvalidOperationException("Fhir:PublicBaseUrl должен быть абсолютным HTTPS-адресом с путём /fhir без учётных данных, параметров и фрагмента.");
        PublicBaseUri = uri;
        PublicBaseUrl = uri.AbsoluteUri.TrimEnd('/');
    }

    public Uri PublicBaseUri { get; }
    public string PublicBaseUrl { get; }

    public string SearchUrl(FhirSearchParameters search, string? cursor)
    {
        var parameters = new List<KeyValuePair<string, string?>>();
        if (search.ResourceType == "ServiceRequest")
        {
            if (search.Status is { } status)
                parameters.Add(new("status", status switch
                {
                    Domain.MedicalRecords.LaboratoryAnalysisRequestStatus.Active => "active",
                    Domain.MedicalRecords.LaboratoryAnalysisRequestStatus.Completed => "completed",
                    _ => throw new InvalidOperationException("Неподдерживаемый статус поиска FHIR.")
                }));
        }
        else if (search.ResourceType == "Observation" && search.BasedOn is { } basedOn)
            parameters.Add(new("based-on", $"ServiceRequest/{basedOn:D}"));
        else throw new InvalidOperationException("Неподдерживаемый тип поиска FHIR.");
        parameters.Add(new("_count", search.Count.ToString(CultureInfo.InvariantCulture)));
        if (cursor is not null) parameters.Add(new("_cursor", cursor));
        return PublicBaseUrl + "/" + search.ResourceType + QueryString.Create(parameters);
    }
}
