using HospitalInformationSystem.Domain.Common;

namespace HospitalInformationSystem.Domain.MedicalRecords.Factories;

public static class LaboratoryAnalysisResultFactory
{
    public static LaboratoryAnalysisResult Create(Guid requestId, Guid patientId, string value,
        DateTimeOffset issuedAtUtc, DateTimeOffset receivedAtUtc, Guid serviceAccountId)
    {
        if (requestId == Guid.Empty || patientId == Guid.Empty || serviceAccountId == Guid.Empty)
            throw new DomainValidationException("Не указан заказ, пациент или сервисный автор результата.");
        var normalizedValue = LaboratoryAnalysisRules.NormalizeValue(value);
        var issued = LaboratoryAnalysisRules.NormalizeIssuedAt(issuedAtUtc);
        Guid id;
        do { id = Guid.NewGuid(); } while (id == requestId);
        return new LaboratoryAnalysisResult
        {
            Id = id, LaboratoryAnalysisRequestId = requestId, PatientId = patientId,
            Value = normalizedValue, IssuedAtUtc = issued, ReceivedAtUtc = receivedAtUtc.ToUniversalTime(),
            CreatedByServiceAccountId = serviceAccountId
        };
    }
}
