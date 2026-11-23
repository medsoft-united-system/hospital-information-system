namespace HospitalInformationSystem.Models.MedicalRecords;

public sealed record PrescriptionCancelledPayload(Guid PrescriptionId, string Text);
