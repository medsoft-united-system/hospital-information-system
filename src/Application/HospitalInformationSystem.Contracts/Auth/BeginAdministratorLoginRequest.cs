namespace HospitalInformationSystem.Contracts.Auth;

public sealed record BeginAdministratorLoginRequest(string UserName, string Password);
