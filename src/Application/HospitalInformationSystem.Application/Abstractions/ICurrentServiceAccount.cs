using HospitalInformationSystem.Application.ServiceAccounts;

namespace HospitalInformationSystem.Application.Abstractions;

public interface ICurrentServiceAccount
{
    ServiceAccountIdentity RequireIdentity();
}
