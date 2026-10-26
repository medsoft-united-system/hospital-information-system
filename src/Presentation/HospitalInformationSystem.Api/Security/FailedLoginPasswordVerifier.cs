using HospitalInformationSystem.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace HospitalInformationSystem.Api.Security;

public sealed class FailedLoginPasswordVerifier
{
    private readonly HospitalUser _user = new();
    private readonly Argon2idPasswordHasher _hasher;
    private readonly string _hash;

    public FailedLoginPasswordVerifier(IOptions<Argon2idOptions> options)
    {
        _hasher = new Argon2idPasswordHasher(options);
        _hash = _hasher.HashPassword(_user, Guid.NewGuid().ToString());
    }

    public void Verify(string password) => _hasher.VerifyHashedPassword(_user, _hash, password);
}
