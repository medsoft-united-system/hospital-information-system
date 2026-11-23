using System.Globalization;
using System.Threading.RateLimiting;
using HospitalInformationSystem.Application.Contracts.Abstractions;
using HospitalInformationSystem.Application.Contracts.Audit;
using HospitalInformationSystem.Application.Models.Audit;
using HospitalInformationSystem.Application.Models.Auth;
using HospitalInformationSystem.Application.Models.Security;
using HospitalInformationSystem.Infrastructure.Identity;
using HospitalInformationSystem.Infrastructure.Identity.Tokens;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;

namespace HospitalInformationSystem.Api.Security;

public static class ApiSecurityRegistration
{
    public const long MaxJsonBodyBytes = 64 * 1024;
    public const long MaxUploadBodyBytes = 1024 * 1024 + 64 * 1024;

    public static IServiceCollection AddApiSecurity(this IServiceCollection services,
        IConfiguration configuration, IHostEnvironment environment)
    {
        var local = environment.IsDevelopment() || environment.IsEnvironment("Testing");
        var loginPermitLimit = configuration.GetValue<int?>("Security:LoginRateLimit:PermitLimit") ?? 10;
        if (loginPermitLimit is < 1 or > 1000)
            throw new InvalidOperationException("Security:LoginRateLimit:PermitLimit must be between 1 and 1000.");
        var origins = (configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [])
            .Select(origin => NormalizeOrigin(origin, local)).Distinct(StringComparer.Ordinal).ToArray();
        services.AddHttpContextAccessor();
        services.AddAdministratorSecurity(configuration, environment);
        services.AddScoped<IAuditActorAccessor, HttpAuditActorAccessor>();
        services.AddScoped<AuthenticationAuditService>();
        services.AddScoped<ICurrentMedicalWorker, CurrentMedicalWorker>();
        services.AddScoped<IAuthorizationHandler, MedicalRoleAuthorizationHandler>();
        services.Configure<JwtTokenOptions>(configuration.GetSection("Authentication:Jwt"));
        if (environment.IsEnvironment("Testing"))
            services.PostConfigure<JwtTokenOptions>(options => options.AllowEphemeralKeys = true);
        else if (configuration.GetValue<bool>("Authentication:Jwt:AllowEphemeralKeys"))
            throw new InvalidOperationException("Временные JWT-ключи разрешены только в тестовой среде.");
        services.AddSingleton<JwtKeyMaterial>();
        services.AddScoped<TokenSessionService>();
        services.AddScoped<ITokenSessionService>(provider => provider.GetRequiredService<TokenSessionService>());
        services.AddScoped<RefreshCookieService>();
        services.AddSingleton<AdministratorLoginChallenges>();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<JwtKeyMaterial>((options, keys) =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = keys.ValidationParameters;
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = async context =>
                    {
                        var sessions = context.HttpContext.RequestServices.GetRequiredService<ITokenSessionService>();
                        if (context.Principal is null || !await sessions.ValidatePrincipalAsync(context.Principal,
                                context.HttpContext.RequestAborted))
                            context.Fail("Сессия завершена или права доступа изменены.");
                    }
                };
            });
        services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
            options.AddPolicy(ApiSecurityPolicies.Administrator, policy =>
                policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                    .RequireRole(ApplicationRoles.Administrator));
            options.AddPolicy(ApiSecurityPolicies.Clinical, policy =>
                policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                    .RequireRole(ApplicationRoles.MedicalWorker));
            options.AddPolicy(ApiSecurityPolicies.ManagePatients, policy =>
                policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                    .RequireRole(ApplicationRoles.MedicalWorker)
                    .AddRequirements(new MedicalRoleRequirement(MedicalWorkerRole.Doctor, MedicalWorkerRole.Nurse)));
            options.AddPolicy(ApiSecurityPolicies.EditMedicalRecord, policy =>
                policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                    .RequireRole(ApplicationRoles.MedicalWorker)
                    .AddRequirements(new MedicalRoleRequirement(MedicalWorkerRole.Doctor)));
            options.AddPolicy(ApiSecurityPolicies.CatalogRead, policy =>
                policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                    .RequireRole(ApplicationRoles.Administrator, ApplicationRoles.MedicalWorker));
        });
        services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-CSRF-TOKEN";
            options.Cookie.Name = local ? "MedSoft.Antiforgery" : "__Host-MedSoft.Antiforgery";
            options.Cookie.HttpOnly = true;
            options.Cookie.IsEssential = true;
            options.Cookie.Path = "/";
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = local ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        });
        services.AddCors(options => options.AddDefaultPolicy(policy => policy.WithOrigins(origins)
            .WithMethods("GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS")
            .WithHeaders("Content-Type", "X-CSRF-TOKEN", "Authorization", "X-Setup-Token", "X-Medical-Worker-Id",
                BrowserTabSession.HeaderName)
            .AllowCredentials()));
        services.Configure<FormOptions>(options =>
        {
            options.MultipartBodyLengthLimit = MaxUploadBodyBytes;
            options.MultipartHeadersLengthLimit = 4096;
            options.KeyLengthLimit = 128;
            options.ValueLengthLimit = 1024;
            options.ValueCountLimit = 16;
        });
        services.AddHsts(options =>
        {
            options.MaxAge = TimeSpan.FromDays(180);
            options.IncludeSubDomains = false;
        });
        services.AddSingleton<FailedLoginPasswordVerifier>();
        services.AddRateLimiter(options =>
        {
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/fhir")
                    ? RateLimitPartition.GetConcurrencyLimiter("api", _ => new ConcurrencyLimiterOptions
                    {
                        PermitLimit = 32,
                        QueueLimit = 0
                    })
                    : RateLimitPartition.GetNoLimiter("other"));
            options.AddPolicy(ApiSecurityPolicies.LoginRateLimit, context =>
                RateLimitPartition.GetSlidingWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new SlidingWindowRateLimiterOptions
                    {
                        PermitLimit = loginPermitLimit,
                        Window = TimeSpan.FromMinutes(5),
                        SegmentsPerWindow = 5,
                        QueueLimit = 0
                    }));
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, cancellationToken) =>
            {
                var request = context.HttpContext.Request;
                if (HttpMethods.IsPost(request.Method) && AuthenticationAuditService.IsLoginPath(request.Path))
                    await context.HttpContext.RequestServices.GetRequiredService<AuthenticationAuditService>()
                        .LoginRateLimitedAsync(cancellationToken);
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    context.HttpContext.Response.Headers.RetryAfter =
                        Math.Ceiling(retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                await Results.Problem(statusCode: StatusCodes.Status429TooManyRequests,
                    title: "Слишком много запросов.", detail: "Повторите попытку позднее.")
                    .ExecuteAsync(context.HttpContext);
            };
        });
        return services;
    }

    private static string NormalizeOrigin(string origin, bool allowHttp)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps && (!allowHttp || uri.Scheme != Uri.UriSchemeHttp) ||
            uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.Host.Contains('*'))
            throw new InvalidOperationException("Cors:AllowedOrigins must contain explicit HTTPS origins (HTTP is allowed locally).");
        return uri.GetLeftPart(UriPartial.Authority);
    }

}
