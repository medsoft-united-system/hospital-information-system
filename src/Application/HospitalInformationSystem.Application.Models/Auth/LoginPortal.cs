using System.Text.Json.Serialization;

namespace HospitalInformationSystem.Application.Models.Auth;

[JsonConverter(typeof(JsonStringEnumConverter<LoginPortal>))]
public enum LoginPortal
{
    [JsonStringEnumMemberName("medical-worker")]
    MedicalWorker,

    [JsonStringEnumMemberName("administrator")]
    Administrator
}
