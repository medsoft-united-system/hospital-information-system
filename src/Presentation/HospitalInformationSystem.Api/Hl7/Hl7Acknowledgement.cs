namespace HospitalInformationSystem.Api.Hl7;

public sealed record Hl7Acknowledgement(
    Hl7AcknowledgementCode Code,
    string Text,
    string? ErrorCode = null);

public enum Hl7AcknowledgementCode
{
    AA,
    AE,
    AR
}
