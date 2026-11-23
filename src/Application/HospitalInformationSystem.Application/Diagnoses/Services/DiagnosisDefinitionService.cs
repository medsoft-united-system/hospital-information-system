using HospitalInformationSystem.Application.Common;
using HospitalInformationSystem.Models.Common;
using HospitalInformationSystem.Models.Diagnoses;

namespace HospitalInformationSystem.Application.Diagnoses.Services;

public static class DiagnosisDefinitionService
{
    public static void Update(DiagnosisDefinition diagnosis, string code, string name, DateTimeOffset now)
    {
        var validCode = code?.Trim();
        var validName = name?.Trim();
        TextValidation.RejectNullCharacter(validCode, "Код диагноза");
        TextValidation.RejectNullCharacter(validName, "Название диагноза");
        if (validCode is null || validCode.Length is < DiagnosisLimits.MinCodeLength or > DiagnosisLimits.MaxCodeLength)
            throw new DomainValidationException(
                $"Код диагноза должен содержать от {DiagnosisLimits.MinCodeLength} до {DiagnosisLimits.MaxCodeLength} символов.");
        if (validName is null || validName.Length is < DiagnosisLimits.MinNameLength or > DiagnosisLimits.MaxNameLength)
            throw new DomainValidationException(
                $"Название диагноза должно содержать от {DiagnosisLimits.MinNameLength} до {DiagnosisLimits.MaxNameLength} символов.");
        diagnosis.Code = validCode;
        diagnosis.Name = validName;
        diagnosis.UpdatedAtUtc = now.ToUniversalTime();
    }
}
