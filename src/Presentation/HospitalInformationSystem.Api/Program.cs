using HospitalInformationSystem.Api.Middleware;
using HospitalInformationSystem.Api.Security;
using HospitalInformationSystem.Application.DI;
using HospitalInformationSystem.Infrastructure.Contexts;
using HospitalInformationSystem.Infrastructure.DI;
using HospitalInformationSystem.Infrastructure.Initializers;
using HospitalInformationSystem.Infrastructure.Identity;
using HospitalInformationSystem.Infrastructure.Identity.Tokens;
using HospitalInformationSystem.Infrastructure.ServiceAccounts.Tokens;
using System.Security.Authentication;
using HospitalInformationSystem.Api.Fhir;
using HospitalInformationSystem.Api.Hl7;
using HospitalInformationSystem.Api.Hl7.Transport;

var builder = WebApplication.CreateBuilder(args);
if (builder.Environment.IsDevelopment())
    builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
        .AddUserSecrets<Program>(optional: true).AddEnvironmentVariables().AddCommandLine(args);
builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.ConfigureHttpsDefaults(https => https.SslProtocols = SslProtocols.Tls13);
    options.Limits.MaxRequestBodySize = ApiSecurityRegistration.MaxUploadBodyBytes;
    options.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
});
var connectionString = builder.Configuration.GetConnectionString("Postgres");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("Configure ConnectionStrings:Postgres using user-secrets, environment variables or appsettings.Local.json in Development.");
builder.Services.AddApplication();
builder.Services.AddInfrastructure(connectionString);
builder.Services.Configure<Argon2idOptions>(builder.Configuration.GetSection(Argon2idOptions.SectionName));
builder.Services.AddControllers().AddJsonOptions(options => options.JsonSerializerOptions.MaxDepth = 32);
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddApiSecurity(builder.Configuration, builder.Environment);
builder.Services.AddServiceAccountSecurity(builder.Configuration);
builder.Services.AddFhirApi();
builder.Services.AddHl7ImportCodec();
builder.Services.AddHl7MllpTransport(builder.Configuration);

var app = builder.Build();
_ = app.Services.GetRequiredService<JwtKeyMaterial>();
_ = app.Services.GetRequiredService<ServiceTokenValidationParameters>().ValidationParameters;
_ = app.Services.GetRequiredService<FhirEndpointOptions>();
app.UseMiddleware<ApiSecurityHeadersMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseMiddleware<ServiceAuthProtocolMiddleware>();
app.UseMiddleware<FhirProtocolMiddleware>();
if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing"))
    app.UseHsts();
app.UseMiddleware<ApiTransportSecurityMiddleware>();
app.UseRouting();
app.UseCors();
app.UseMiddleware<BrowserTabSessionMiddleware>();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<ApiRequestLimitsMiddleware>();
app.UseMiddleware<ApiAntiforgeryMiddleware>();
app.MapControllers();
if (app.Environment.IsDevelopment())
    app.MapOpenApi().RequireAuthorization(ApiSecurityPolicies.Administrator);
app.MapGet("/health/live", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.MapGet("/health/ready", async (HospitalDbContext db, CancellationToken cancellationToken) =>
    await db.Database.CanConnectAsync(cancellationToken)
        ? Results.Ok(new { status = "ready" })
        : Results.Problem("PostgreSQL недоступен.", statusCode: 503)).AllowAnonymous();

if (app.Configuration.GetValue<bool>("Database:ApplyMigrations"))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>()
        .InitializeAsync(app.Configuration.GetValue<bool>("Database:Seed"), CancellationToken.None);
}

await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<IdentityBootstrapper>()
        .InitializeRolesAsync(CancellationToken.None);
}

app.Run();

public partial class Program;
