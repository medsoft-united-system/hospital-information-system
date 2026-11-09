using System.Text.Json.Serialization;

namespace HospitalInformationSystem.Contracts.LaboratoryTests;

public sealed record SetLaboratoryTestActiveRequest([property: JsonRequired] bool IsActive);
