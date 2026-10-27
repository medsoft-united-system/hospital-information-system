using System;
using HospitalInformationSystem.Infrastructure.Identity.Tokens;
using Microsoft.Extensions.Options;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class TokenSigningFixture : IDisposable
{
    public IOptions<JwtTokenOptions> Options { get; } = Microsoft.Extensions.Options.Options.Create(
        new JwtTokenOptions { AllowEphemeralKeys = true });
    public JwtKeyMaterial Keys { get; }

    public TokenSigningFixture() => Keys = new JwtKeyMaterial(Options, TimeProvider.System);

    public void Dispose() => Keys.Dispose();
}
