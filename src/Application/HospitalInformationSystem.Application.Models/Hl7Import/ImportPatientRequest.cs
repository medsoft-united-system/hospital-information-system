namespace HospitalInformationSystem.Application.Models.Hl7Import;

public sealed record ImportPatientRequest(
    string MessageControlId,
    ImportedPatientInput Patient,
    ImportConnectionContext Context);
