namespace HospitalInformationSystem.Api.Fhir;

public sealed class FhirHttpException(int statusCode, string machineCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string MachineCode { get; } = machineCode;
}
