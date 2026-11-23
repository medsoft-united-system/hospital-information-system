using System.Text.Json.Serialization;

namespace HospitalInformationSystem.Application.Models.Patients;

public sealed record SavePatientRequest(
    string FirstName, string LastName, string? MiddleName,
    [property: JsonRequired] DateOnly BirthDate,
    [property: JsonRequired] AdministrativeSex Sex, string? Comment);
