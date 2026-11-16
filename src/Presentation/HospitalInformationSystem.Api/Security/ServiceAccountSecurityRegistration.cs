using System.Threading.RateLimiting;
using HospitalInformationSystem.Application.Hl7Import;
using HospitalInformationSystem.Application.ServiceAccounts;
using HospitalInformationSystem.Contracts.Auth;
using HospitalInformationSystem.Domain.ServiceAccounts;
using HospitalInformationSystem.Contracts.ServiceAuth;
using HospitalInformationSystem.Infrastructure.ServiceAccounts.Tokens;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace HospitalInformationSystem.Api.Security;

public static class ServiceAccountSecurityRegistration
{
    public static IServiceCollection AddServiceAccountSecurity(this IServiceCollection services,
        IConfiguration configuration)
    {
        var permitLimit = configuration.GetValue<int?>("Security:ServiceAuthRateLimit:PermitLimit") ?? 10;
        if (permitLimit is < 1 or > 1000)
            throw new InvalidOperationException("Security:ServiceAuthRateLimit:PermitLimit must be between 1 and 1000.");
        services.AddOptions<ServiceTokenOptions>().Bind(configuration.GetSection(ServiceTokenOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ServiceTokenOptions>, ServiceTokenOptionsValidator>());
        services.AddSingleton<ServiceTokenValidationParameters>();
        services.AddScoped<ServiceTokenSessionService>();
        services.AddScoped<ServiceTokenValidator>();
        services.AddScoped<IServiceTokenValidator>(provider => provider.GetRequiredService<ServiceTokenValidator>());
        services.AddScoped<IHl7ImportTokenValidator>(provider => provider.GetRequiredService<ServiceTokenValidator>());
        services.AddScoped<ServiceCredentialReader>();
        services.AddAuthentication().AddJwtBearer(ServiceAuthenticationDefaults.Scheme);
        services.AddOptions<JwtBearerOptions>(ServiceAuthenticationDefaults.Scheme)
            .Configure<ServiceTokenValidationParameters>((options, validation) =>
            {
                options.MapInboundClaims = false;
                options.SaveToken = false;
                options.IncludeErrorDetails = false;
                options.TokenValidationParameters = validation.ValidationParameters;
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = async context =>
                    {
                        var sessions = context.HttpContext.RequestServices.GetRequiredService<ServiceTokenSessionService>();
                        if (context.Principal is null || !await sessions.ValidatePrincipalAsync(context.Principal,
                                context.HttpContext.RequestAborted))
                            context.Fail("Сервисная сессия недействительна.");
                    },
                    OnChallenge = context =>
                    {
                        context.HandleResponse();
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        context.Response.Headers.WWWAuthenticate = "Bearer";
                        return Task.CompletedTask;
                    },
                    OnForbidden = context =>
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        return Task.CompletedTask;
                    }
                };
            });
        services.AddAuthorization(options =>
        {
            foreach (var name in new[] { ServiceAuthenticationDefaults.Policies.FhirRead,
                         ServiceAuthenticationDefaults.Policies.ObservationCreate })
                options.AddPolicy(name, policy => policy.AddAuthenticationSchemes(ServiceAuthenticationDefaults.Scheme)
                    .RequireAuthenticatedUser()
                    .RequireAssertion(context => ServiceAccountPrincipal.TryGetIdentity(context.User)?.Role == ServiceAccountRole.Lis));
            options.AddPolicy(ServiceAuthenticationDefaults.Policies.ConnectionProbe, policy =>
                policy.AddAuthenticationSchemes(ServiceAuthenticationDefaults.Scheme, JwtBearerDefaults.AuthenticationScheme)
                    .RequireAuthenticatedUser()
                    .RequireAssertion(context => ServiceAccountPrincipal.TryGetIdentity(context.User) is not null ||
                        context.User.IsInRole(ApplicationRoles.Administrator) || context.User.IsInRole(ApplicationRoles.MedicalWorker)));
        });
        services.AddRateLimiter(options => options.AddPolicy(ServiceAuthProtocol.RateLimitPolicy, context =>
            RateLimitPartition.GetSlidingWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "local",
                _ => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = permitLimit, Window = TimeSpan.FromMinutes(5), SegmentsPerWindow = 5, QueueLimit = 0
                })));
        return services;
    }
}
