namespace HospitalInformationSystem.Models.Security;

public sealed record ConfirmSetupRequest(Guid ChallengeId, string Code);
