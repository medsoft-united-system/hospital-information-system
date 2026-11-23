using HospitalInformationSystem.Models.Common;
using HospitalInformationSystem.Models.MedicalRecords;

namespace HospitalInformationSystem.Application.MedicalRecords.Factories;

public static class MedicalRecordDiagnosisFactory
{
    public static MedicalRecordDiagnosis Create(
        Guid medicalRecordId, Guid diagnosisDefinitionId, string code, string name, Guid authorId, DateTimeOffset at)
    {
        if (medicalRecordId == Guid.Empty || diagnosisDefinitionId == Guid.Empty || authorId == Guid.Empty)
            throw new DomainValidationException("Не указаны карта, диагноз справочника или автор.");
        var validCode = code?.Trim();
        var validName = name?.Trim();
        if (!MedicalRecordDiagnosisRules.IsValidCode(validCode))
            throw new DomainValidationException("Код диагноза должен содержать от 3 до 32 символов.");
        if (!MedicalRecordDiagnosisRules.IsValidName(validName))
            throw new DomainValidationException("Название диагноза: требуется от 3 до 256 символов.");
        return new MedicalRecordDiagnosis
        {
            Id = Guid.NewGuid(), MedicalRecordId = medicalRecordId, DiagnosisDefinitionId = diagnosisDefinitionId,
            DiagnosisCodeSnapshot = validCode!, DiagnosisNameSnapshot = validName!,
            AddedAtUtc = at.ToUniversalTime(), AddedByMedicalWorkerId = authorId
        };
    }

    public static MedicalRecordDiagnosis CreateForService(Guid medicalRecordId, Guid diagnosisDefinitionId,
        string sourceCode, string sourceTitle, string codingSystem, DateTimeOffset diagnosedAt,
        string diagnosisType, Guid serviceAccountId, DateTimeOffset at)
    {
        if (medicalRecordId == Guid.Empty || diagnosisDefinitionId == Guid.Empty || serviceAccountId == Guid.Empty)
            throw new DomainValidationException("Не указаны карта, диагноз справочника или сервисный автор.");
        var code = sourceCode?.Trim();
        var title = sourceTitle?.Trim();
        var system = codingSystem?.Trim();
        var type = diagnosisType?.Trim();
        if (!MedicalRecordDiagnosisRules.IsValidCode(code) || !MedicalRecordDiagnosisRules.IsValidName(title))
            throw new DomainValidationException("Код диагноза: от 3 до 32 символов, название: от 3 до 256 символов.");
        if (!MedicalRecordDiagnosisRules.IsValidMetadata(system) || !MedicalRecordDiagnosisRules.IsValidMetadata(type))
            throw new DomainValidationException("Укажите систему кодирования и тип диагноза длиной до 32 символов.");
        return new MedicalRecordDiagnosis
        {
            Id = Guid.NewGuid(), MedicalRecordId = medicalRecordId, DiagnosisDefinitionId = diagnosisDefinitionId,
            DiagnosisCodeSnapshot = code!, DiagnosisNameSnapshot = title!, AddedAtUtc = at.ToUniversalTime(),
            AddedByServiceAccountId = serviceAccountId, DiagnosedAtUtc = diagnosedAt.ToUniversalTime(),
            DiagnosedAtOffsetMinutes = checked((int)diagnosedAt.Offset.TotalMinutes),
            CodingSystem = system, DiagnosisType = type
        };
    }
}
