using HospitalInformationSystem.Application.MedicalRecords.Services;
using HospitalInformationSystem.Application.Models.Common;
using HospitalInformationSystem.Application.Models.MedicalRecords;
using HospitalInformationSystem.Application.Models.MedicalWorkers;

namespace HospitalInformationSystem.Application.MedicalRecords.Factories;

public static class MedicalRecordFactory
{
    public static MedicalRecord Create(Guid patientId, MedicalWorker author, DateTimeOffset now)
    {
        MedicalRecordService.ValidateAuthor(author);
        if (patientId == Guid.Empty)
            throw new DomainValidationException("Не указан пациент.");
        var id = Guid.NewGuid();
        var at = now.ToUniversalTime();
        var created = MedicalRecordHistoryEventFactory.Create(id, 1, MedicalRecordEventType.MedicalRecordCreated,
            id, new MedicalRecordCreatedPayload(patientId), author, at);
        return new MedicalRecord
        {
            Id = id,
            PatientId = patientId,
            CreatedAtUtc = at,
            Version = 1,
            HistoryEvents = Array.AsReadOnly([created])
        };
    }

    public static MedicalRecord CreateForService(Guid patientId, Guid serviceAccountId, string serviceAccountName,
        DateTimeOffset now)
    {
        if (patientId == Guid.Empty)
            throw new DomainValidationException("Не указан пациент.");
        var id = Guid.NewGuid();
        var at = now.ToUniversalTime();
        var created = MedicalRecordHistoryEventFactory.CreateForService(id, 1,
            MedicalRecordEventType.MedicalRecordCreated, id, new MedicalRecordCreatedPayload(patientId),
            serviceAccountId, serviceAccountName, at);
        return new MedicalRecord
        {
            Id = id,
            PatientId = patientId,
            CreatedAtUtc = at,
            Version = 1,
            HistoryEvents = Array.AsReadOnly([created])
        };
    }
}
