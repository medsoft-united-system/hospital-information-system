using System.Security.Claims;
using HospitalInformationSystem.Contracts.Abstractions;
using HospitalInformationSystem.Models.Auth;

namespace HospitalInformationSystem.Api.Security;

public sealed class CurrentMedicalWorker(IHttpContextAccessor accessor) : ICurrentMedicalWorker
{
    public Guid RequireId()
    {
        var principal = accessor.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true ||
            !Guid.TryParse(principal.FindFirstValue(AuthClaimTypes.MedicalWorkerId), out var id) || id == Guid.Empty)
            throw new ForbiddenOperationException();
        return id;
    }
}
