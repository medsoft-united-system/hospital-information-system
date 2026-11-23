using HospitalInformationSystem.Application.Models.ServiceAuth;
using HospitalInformationSystem.Infrastructure.Identity.Tokens;
using Microsoft.Extensions.Options;

namespace HospitalInformationSystem.Infrastructure.ServiceAccounts.Tokens;

public sealed class ServiceTokenOptions
{
    public const string SectionName = "Authentication:ServiceJwt";
    public string Issuer { get; set; } = ServiceAuthenticationDefaults.Issuer;
    public string Audience { get; set; } = ServiceAuthenticationDefaults.Audience;
    public int AccessTokenMinutes { get; set; } = 5;
    public int RefreshTokenDays { get; set; } = 7;
}

public sealed class ServiceTokenOptionsValidator(IOptions<JwtTokenOptions> humanOptions)
    : IValidateOptions<ServiceTokenOptions>
{
    public ValidateOptionsResult Validate(string? name, ServiceTokenOptions options)
    {
        if (!ValidIdentifier(options.Issuer) || !ValidIdentifier(options.Audience) ||
            string.Equals(options.Audience, humanOptions.Value.Audience, StringComparison.Ordinal) ||
            options.AccessTokenMinutes is < 1 or > 15 || options.RefreshTokenDays is < 1 or > 30)
            return ValidateOptionsResult.Fail("Проверьте отдельные issuer/audience и сроки сервисных JWT/refresh.");
        return ValidateOptionsResult.Success;
    }

    private static bool ValidIdentifier(string? value) => value is { Length: > 0 and <= 256 } &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) && !value.Any(char.IsControl);
}
