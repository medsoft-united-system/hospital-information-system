using System.Text.Json.Serialization;

namespace HospitalInformationSystem.Application.Models.Patients;

[JsonConverter(typeof(JsonStringEnumConverter<AdministrativeSex>))]
public enum AdministrativeSex
{
    Male,
    Female,
    Unknown
}
