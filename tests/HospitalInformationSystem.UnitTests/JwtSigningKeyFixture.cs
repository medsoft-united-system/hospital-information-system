using HospitalInformationSystem.Infrastructure.Identity.Tokens;
using Microsoft.Extensions.Options;

namespace HospitalInformationSystem.UnitTests;

public sealed class JwtSigningKeyFixture : IDisposable
{
    public JwtTokenOptions Options { get; } = new() { AllowEphemeralKeys = true };
    public JwtKeyMaterial Keys { get; }

    public JwtSigningKeyFixture() => Keys = new JwtKeyMaterial(Microsoft.Extensions.Options.Options.Create(Options),
        TimeProvider.System);

    public void Dispose() => Keys.Dispose();
}
