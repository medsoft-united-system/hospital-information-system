using System.Text.Json.Serialization;

namespace HospitalInformationSystem.Application.Models.LaboratoryTests;

public sealed record SetLaboratoryTestActiveRequest([property: JsonRequired] bool IsActive, uint Version = 0);
