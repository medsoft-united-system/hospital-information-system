using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using HospitalInformationSystem.Application.Models.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalInformationSystem.IntegrationTests;

internal static class BusinessTestAuthentication
{
    public const string Scheme = "BusinessTests";

    public static HttpClient CreateBusinessClient(this WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        var csrf = client.GetFromJsonAsync<AntiforgeryTokenDto>("/api/auth/csrf").GetAwaiter().GetResult()!;
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf.RequestToken);
        return client;
    }

    public static void AddBusinessTestAuthentication(this IWebHostBuilder builder) =>
        builder.ConfigureTestServices(services =>
        {
            services.AddScoped<IAuthorizationHandler, BusinessMedicalRoleHandler>();
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = Scheme;
                options.DefaultChallengeScheme = Scheme;
                options.DefaultForbidScheme = Scheme;
                options.SchemeMap[JwtBearerDefaults.AuthenticationScheme].HandlerType = typeof(BusinessTestAuthenticationHandler);
            })
            .AddScheme<AuthenticationSchemeOptions, BusinessTestAuthenticationHandler>(Scheme, _ => { });
        });
}
