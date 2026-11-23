using HospitalInformationSystem.Application.Models.Diagnoses;

namespace HospitalInformationSystem.Application.Models.Diagnoses;

public static class DiagnosisLimits
{
    public const int MinCodeLength = DiagnosisTextLimits.MinCodeLength;
    public const int MaxCodeLength = DiagnosisTextLimits.MaxCodeLength;
    public const int MinNameLength = DiagnosisTextLimits.MinNameLength;
    public const int MaxNameLength = DiagnosisTextLimits.MaxNameLength;
}
