namespace HospitalInformationSystem.Models.Hl7Import;

public static class Hl7ImportProtocol
{
    public const string ContractVersion = "C4-v1";
    public const string Version = "2.8";
    public const string Role = "HL7Import";
    public const string SourceSystem = "LAB4_XML";
    public const string SendingApplication = "MEDSOFT_IMPORT";
    public const string ReceivingApplication = "HIS";
    public const string ReceivingFacility = "MEDSOFT";
    public const string CharacterSet = "UNICODE UTF-8";
    public const string PatientMessageType = "ADT^A28^ADT_A05";
    public const string ProbeMessageType = "NMD^N02^NMD_N02";
    public const string CredentialType = "JWT^JSON Web Token^99MEDSOFT";
    public const string CredentialMediaType = "application";
    public const string CredentialMediaSubtype = "jwt";
    public const string CredentialEncoding = "Base64";
    public const string DiagnosisCodingSystem = "I10";
    public const string UnspecifiedDiagnosisType = "U";
    public const string ProbeSuccess = "CONNECTED";
    public const string DefaultSourceTimeZone = "Europe/Moscow";
    public const int DefaultMllpPort = 2575;
    public const int MaxPayloadBytes = 1024 * 1024;
    public const int MaxDiagnoses = 100;
    public const byte FrameStart = 0x0B;
    public const byte FrameEnd = 0x1C;
    public const byte SegmentTerminator = 0x0D;
}
