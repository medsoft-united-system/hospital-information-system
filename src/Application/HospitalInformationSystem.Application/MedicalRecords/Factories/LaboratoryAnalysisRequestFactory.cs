using HospitalInformationSystem.Models.Common;
using HospitalInformationSystem.Models.LaboratoryTests;
using HospitalInformationSystem.Models.MedicalRecords;

namespace HospitalInformationSystem.Application.MedicalRecords.Factories;

public static class LaboratoryAnalysisRequestFactory
{
    public static LaboratoryAnalysisRequest Create(Guid medicalRecordId, Guid patientId,
        LaboratoryTestDefinition definition, string? comment, Guid requesterId, DateTimeOffset authoredAtUtc)
    {
        if (medicalRecordId == Guid.Empty || patientId == Guid.Empty || requesterId == Guid.Empty ||
            definition.Id == Guid.Empty)
            throw new DomainValidationException("Не указаны карта, пациент, назначивший врач или анализ справочника.");
        if (!definition.IsActive)
            throw new BusinessRuleConflictException("Отключённый анализ нельзя назначить.");
        if (!LaboratoryTestDefinitionRules.IsValidCode(definition.LoincCode) ||
            !LaboratoryTestDefinitionRules.IsValidDisplayName(definition.DisplayName))
            throw new DomainValidationException("В справочнике анализа некорректны код LOINC или название.");
        return new LaboratoryAnalysisRequest
        {
            Id = Guid.NewGuid(), MedicalRecordId = medicalRecordId, PatientId = patientId,
            RequestedByMedicalWorkerId = requesterId, LaboratoryTestDefinitionId = definition.Id,
            LoincCodeSnapshot = definition.LoincCode.Trim(), DisplayNameSnapshot = definition.DisplayName.Trim(),
            Comment = LaboratoryAnalysisRules.NormalizeComment(comment), AuthoredAtUtc = authoredAtUtc.ToUniversalTime()
        };
    }
}
