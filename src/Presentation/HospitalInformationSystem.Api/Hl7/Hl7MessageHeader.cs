namespace HospitalInformationSystem.Api.Hl7;

public sealed record Hl7MessageHeader
{
    public required string MessageType { get; init; }
    public required string TriggerEvent { get; init; }
    public required string MessageStructure { get; init; }
    public required string ControlId { get; init; }
    public required string SendingApplication { get; init; }
    public required string SendingFacility { get; init; }
    public required string ReceivingApplication { get; init; }
    public required string ReceivingFacility { get; init; }
    public required DateTimeOffset SentAt { get; init; }
}
