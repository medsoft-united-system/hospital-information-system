using HospitalInformationSystem.Domain.Diagnoses;

namespace HospitalInformationSystem.Contracts.Diagnoses;

public static class DiagnosisLimits
{
    public const int MinCodeLength = DiagnosisTextLimits.MinCodeLength;
    public const int MaxCodeLength = DiagnosisTextLimits.MaxCodeLength;
    public const int MinNameLength = DiagnosisTextLimits.MinNameLength;
    public const int MaxNameLength = DiagnosisTextLimits.MaxNameLength;
}
