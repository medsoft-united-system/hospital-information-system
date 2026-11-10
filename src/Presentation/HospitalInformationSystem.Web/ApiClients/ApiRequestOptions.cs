namespace HospitalInformationSystem.Web.ApiClients;

internal static class ApiRequestOptions
{
    internal static readonly HttpRequestOptionsKey<bool> NoAutomaticRetry = new("MedSoft.NoAutomaticRetry");
}
