using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using HospitalInformationSystem.Infrastructure.Identity;
using HospitalInformationSystem.Models.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace HospitalInformationSystem.Api.Security;

public static class AdministratorSecurityRegistration
{
    public static IServiceCollection AddAdministratorSecurity(this IServiceCollection services,
        IConfiguration configuration, IHostEnvironment environment)
    {
        services.Configure<AdministratorSetupOptions>(configuration.GetSection("Authentication:Setup"));
        var setupTokenFile = configuration["Authentication:Setup:TokenFile"];
        if (!string.IsNullOrWhiteSpace(setupTokenFile))
        {
            var token = File.ReadAllText(setupTokenFile).Trim();
            if (token.Length is < 32 or > 256 || token.Any(char.IsControl))
                throw new InvalidOperationException("Файл первоначальной настройки должен содержать случайный токен длиной 32–256 символов.");
            var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
            services.PostConfigure<AdministratorSetupOptions>(options => options.TokenSha256 = digest);
        }
        services.AddSingleton<TotpCodeService>();
        services.AddScoped(provider => provider.GetRequiredService<IOptions<AdministratorSetupOptions>>().Value);
        var protection = services.AddDataProtection().SetApplicationName("MedSoft");
        var directory = configuration["Security:DataProtection:KeyDirectory"];
        var certificatePath = configuration["Security:DataProtection:CertificatePath"];
        var certificatePassword = configuration["Security:DataProtection:CertificatePassword"];
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(certificatePath))
        {
            if (!environment.IsEnvironment("Testing"))
                throw new InvalidOperationException("Настройте постоянное хранилище Security:DataProtection:KeyDirectory и внешний CertificatePath для защиты ключей.");
            protection.UseEphemeralDataProtectionProvider();
        }
        else
        {
            if (!Path.IsPathFullyQualified(directory) || !Path.IsPathFullyQualified(certificatePath))
                throw new InvalidOperationException("Пути хранилища Data Protection и сертификата должны быть абсолютными.");
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var certificate = X509CertificateLoader.LoadPkcs12FromFile(certificatePath, certificatePassword,
                OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet);
            if (!certificate.HasPrivateKey)
                throw new InvalidOperationException("Для защиты Data Protection нужен сертификат с закрытым ключом.");
            protection.PersistKeysToFileSystem(new DirectoryInfo(directory)).ProtectKeysWithCertificate(certificate);
            services.AddSingleton(certificate);
        }
        return services;
    }
}
