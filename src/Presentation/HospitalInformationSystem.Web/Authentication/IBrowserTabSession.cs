namespace HospitalInformationSystem.Web.Authentication;

public interface IBrowserTabSession
{
    Task<string> GetIdAsync(CancellationToken cancellationToken);
}
