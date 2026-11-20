using System.Text.Json.Serialization;

namespace HospitalInformationSystem.Api.Hl7;

public sealed class Hl7MessageEnvelope(string accessToken)
{
    public required Hl7MessageHeader Header { get; init; }
    [JsonIgnore]
    public string AccessToken { get; } = accessToken;
}
