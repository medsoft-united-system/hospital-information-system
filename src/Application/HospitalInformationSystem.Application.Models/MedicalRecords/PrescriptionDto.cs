namespace HospitalInformationSystem.Application.Models.MedicalRecords;

public sealed record PrescriptionDto(
    Guid Id,
    string Text,
    DateTimeOffset CreatedAtUtc,
    Guid CreatedByMedicalWorkerId,
    Guid? ReplacesPrescriptionId);
