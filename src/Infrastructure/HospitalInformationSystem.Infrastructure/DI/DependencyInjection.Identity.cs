using HospitalInformationSystem.Application.Abstractions;
using HospitalInformationSystem.Contracts.Auth;
using HospitalInformationSystem.Infrastructure.Contexts;
using HospitalInformationSystem.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace HospitalInformationSystem.Infrastructure.DI;

public static partial class DependencyInjection
{
    private static void AddIdentityPersistence(IServiceCollection services)
    {
        services.AddIdentityCore<HospitalUser>(options =>
            {
                options.User.AllowedUserNameCharacters = AccountUserNameRules.AllowedCharacters;
                options.Password.RequiredLength = 12;
                options.Password.RequireDigit = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireNonAlphanumeric = false;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                options.Lockout.AllowedForNewUsers = true;
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<HospitalDbContext>()
            .AddSignInManager()
            .AddUserValidator<AccountUserNameValidator>()
            .AddPasswordValidator<PasswordLengthValidator>();
        services.AddOptions<Argon2idOptions>().ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<Argon2idOptions>, Argon2idOptionsValidator>());
        services.AddScoped<IPasswordHasher<HospitalUser>, Argon2idPasswordHasher>();
        services.AddSingleton<IAccountPasswordGenerator, AccountPasswordGenerator>();
        services.AddScoped<AccountService>();
        services.AddScoped<IAccountService>(provider => provider.GetRequiredService<AccountService>());
        services.AddScoped<IdentityBootstrapper>();
    }
}
