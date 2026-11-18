namespace HospitalInformationSystem.Application.Hl7Import;

public interface IHl7ImportFailureAuditor
{
    Task<bool> RecordAsync(Hl7ImportFailure failure, CancellationToken cancellationToken);
}
