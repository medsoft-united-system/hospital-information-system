namespace HospitalInformationSystem.Application.Models.MedicalRecords;

public static class MedicalRecordDiagnosisRules
{
    public const int MaxMetadataLength = 32;

    public static bool IsValidCode(string? code) =>
        code is { Length: >= 3 and <= 32 } && code.Trim() == code && !code.Contains('\0');

    public static bool IsValidName(string? name) =>
        name is { Length: >= 3 and <= 256 } && name.Trim() == name && !name.Contains('\0');

    public static bool IsValidMetadata(string? value) =>
        value is { Length: >= 1 and <= MaxMetadataLength } && value.Trim() == value && !value.Contains('\0');

    public static bool IsValidClinicalTime(DateTimeOffset? diagnosedAtUtc, int? offsetMinutes) =>
        (diagnosedAtUtc is null && offsetMinutes is null) ||
        (diagnosedAtUtc is { Offset: var offset } && offset == TimeSpan.Zero &&
         offsetMinutes is >= -840 and <= 840);
}
