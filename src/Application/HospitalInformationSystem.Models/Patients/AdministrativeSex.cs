using System.Text.Json.Serialization;

namespace HospitalInformationSystem.Models.Patients;

[JsonConverter(typeof(JsonStringEnumConverter<AdministrativeSex>))]
public enum AdministrativeSex
{
    Male,
    Female,
    Unknown
}
