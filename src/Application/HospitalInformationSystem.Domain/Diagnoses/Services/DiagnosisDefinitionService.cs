using HospitalInformationSystem.Domain.Common;

namespace HospitalInformationSystem.Domain.Diagnoses.Services;

public static class DiagnosisDefinitionService
{
    public static void Update(DiagnosisDefinition diagnosis, string code, string name, DateTimeOffset now)
    {
        var validCode = code?.Trim();
        var validName = name?.Trim();
        TextValidation.RejectNullCharacter(validCode, "Код диагноза");
        TextValidation.RejectNullCharacter(validName, "Название диагноза");
        if (validCode is null || validCode.Length is < DiagnosisTextLimits.MinCodeLength or > DiagnosisTextLimits.MaxCodeLength)
            throw new DomainValidationException(
                $"Код диагноза должен содержать от {DiagnosisTextLimits.MinCodeLength} до {DiagnosisTextLimits.MaxCodeLength} символов.");
        if (validName is null || validName.Length is < DiagnosisTextLimits.MinNameLength or > DiagnosisTextLimits.MaxNameLength)
            throw new DomainValidationException(
                $"Название диагноза должно содержать от {DiagnosisTextLimits.MinNameLength} до {DiagnosisTextLimits.MaxNameLength} символов.");
        diagnosis.Code = validCode;
        diagnosis.Name = validName;
        diagnosis.UpdatedAtUtc = now.ToUniversalTime();
    }
}
