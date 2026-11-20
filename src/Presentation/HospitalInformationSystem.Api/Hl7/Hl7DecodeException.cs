namespace HospitalInformationSystem.Api.Hl7;

public enum Hl7FailureKind
{
    Protocol,
    Authentication,
    Validation
}

public sealed class Hl7DecodeException(Hl7FailureKind kind, Hl7MessageHeader? header = null)
    : Exception(kind switch
    {
        Hl7FailureKind.Authentication => "HL7 authentication rejected.",
        Hl7FailureKind.Validation => "HL7 clinical data rejected.",
        _ => "HL7 protocol rejected."
    })
{
    public Hl7FailureKind Kind { get; } = kind;
    public Hl7MessageHeader? Header { get; } = header;
}
