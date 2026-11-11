using System.Text;
using Hl7.Fhir.Serialization;
using Resource = Hl7.Fhir.Model.Resource;

namespace HospitalInformationSystem.Api.Fhir;

public sealed class FhirResponseWriter
{
    public const string ContentType = "application/fhir+json";

    public Task<string> SerializeAsync(Resource resource, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var json = FhirJsonSerializer.Default.SerializeToString(resource);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(json);
    }

    public async Task WriteAsync(HttpContext context, Resource resource, int statusCode = StatusCodes.Status200OK,
        CancellationToken cancellationToken = default)
    {
        var json = await SerializeAsync(resource, cancellationToken);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = ContentType;
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsync(json, Encoding.UTF8, cancellationToken);
    }
}
