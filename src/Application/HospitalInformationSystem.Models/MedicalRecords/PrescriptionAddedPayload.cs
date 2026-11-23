namespace HospitalInformationSystem.Models.MedicalRecords;

public sealed record PrescriptionAddedPayload(Guid PrescriptionId, string Text, Guid? ReplacesPrescriptionId);
