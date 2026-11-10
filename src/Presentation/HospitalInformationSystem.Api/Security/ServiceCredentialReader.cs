using System.Text;
using System.Text.Json;
using Microsoft.Net.Http.Headers;

namespace HospitalInformationSystem.Api.Security;

public sealed class ServiceCredentialReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task<string> ReadAsync(HttpRequest request, string field, CancellationToken cancellationToken)
    {
        ValidateContentType(request.ContentType);
        if (request.QueryString.HasValue) throw ServiceAuthProtocol.Error(400);
        if (request.ContentLength > ServiceAuthProtocol.MaxBodyBytes) throw ServiceAuthProtocol.Error(413);
        var bytes = await ReadBodyAsync(request.Body, cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(StrictUtf8.GetString(bytes),
                new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw ServiceAuthProtocol.Error(400);
            var properties = root.EnumerateObject().ToArray();
            if (properties.Length != 1 || properties[0].Name != field ||
                properties[0].Value.ValueKind != JsonValueKind.String)
                throw ServiceAuthProtocol.Error(400);
            var value = properties[0].Value.GetString();
            if (string.IsNullOrWhiteSpace(value) || value.Length > 1024 || value.Any(char.IsControl))
                throw ServiceAuthProtocol.Error(400);
            for (var index = 0; index < value.Length; index++)
                if (char.IsSurrogate(value[index]) && (!char.IsHighSurrogate(value[index]) ||
                    index + 1 >= value.Length || !char.IsLowSurrogate(value[++index])))
                    throw ServiceAuthProtocol.Error(400);
            return value;
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException or
                                             InvalidOperationException or ArgumentException)
        {
            throw ServiceAuthProtocol.Error(400);
        }
    }

    private static void ValidateContentType(string? contentType)
    {
        if (!MediaTypeHeaderValue.TryParse(contentType, out var media) ||
            !string.Equals(media.MediaType.Value, "application/json", StringComparison.OrdinalIgnoreCase))
            throw ServiceAuthProtocol.Error(415);
        if (media.Parameters.Count > 1 || media.Parameters.Any(parameter =>
                !string.Equals(parameter.Name.Value, "charset", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(HeaderUtilities.RemoveQuotes(parameter.Value).Value, "utf-8",
                    StringComparison.OrdinalIgnoreCase)))
            throw ServiceAuthProtocol.Error(415);
    }

    private static async Task<byte[]> ReadBodyAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var body = new MemoryStream();
        var buffer = new byte[1024];
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0,
                    Math.Min(buffer.Length, ServiceAuthProtocol.MaxBodyBytes + 1 - (int)body.Length)), cancellationToken);
                if (read == 0) return body.ToArray();
                await body.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                if (body.Length > ServiceAuthProtocol.MaxBodyBytes) throw ServiceAuthProtocol.Error(413);
            }
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == 413)
        {
            throw ServiceAuthProtocol.Error(413);
        }
        catch (IOException)
        {
            throw ServiceAuthProtocol.Error(400);
        }
    }
}
