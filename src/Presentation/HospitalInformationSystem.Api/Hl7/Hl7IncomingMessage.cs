using System.Text.Json.Serialization;
using HospitalInformationSystem.Application.Hl7Import;

namespace HospitalInformationSystem.Api.Hl7;

public sealed class Hl7IncomingMessage(string accessToken)
{
    public required Hl7MessageHeader Header { get; init; }
    [JsonIgnore]
    public string AccessToken { get; } = accessToken;
    public ImportedPatientInput? Patient { get; init; }
}
