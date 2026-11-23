namespace HospitalInformationSystem.Application.Models.LaboratoryTests;

public sealed record SaveLaboratoryTestRequest(string LoincCode, string DisplayName, uint? Version = null);
