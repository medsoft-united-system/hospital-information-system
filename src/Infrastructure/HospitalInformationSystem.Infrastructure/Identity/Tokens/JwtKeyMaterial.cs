using System.Security.Cryptography;
using HospitalInformationSystem.Contracts.Auth;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace HospitalInformationSystem.Infrastructure.Identity.Tokens;

public sealed class JwtKeyMaterial : IDisposable
{
    private readonly RSA _signingRsa;
    private readonly RSA _validationRsa;
    private readonly JwtTokenOptions _options;
    private readonly TimeProvider _timeProvider;

    public JwtKeyMaterial(IOptions<JwtTokenOptions> options, TimeProvider timeProvider)
    {
        _options = options.Value;
        _timeProvider = timeProvider;
        if (string.IsNullOrWhiteSpace(_options.Issuer) || string.IsNullOrWhiteSpace(_options.Audience) ||
            string.IsNullOrWhiteSpace(_options.KeyId) || _options.AccessTokenMinutes is < 1 or > 15 ||
            _options.RefreshTokenDays is < 1 or > 30)
            throw new InvalidOperationException("Проверьте issuer, audience, kid и сроки JWT/refresh в конфигурации.");
        _signingRsa = RSA.Create();
        _validationRsa = RSA.Create();
        try
        {
            if (string.IsNullOrWhiteSpace(_options.PrivateKeyPath))
            {
                if (!_options.AllowEphemeralKeys)
                    throw new InvalidOperationException("Укажите внешний RSA private key для подписи JWT.");
                if (!string.IsNullOrWhiteSpace(_options.PublicKeyPath))
                    throw new InvalidOperationException("Открытый JWT-ключ требует соответствующего закрытого ключа.");
                _signingRsa.KeySize = 3072;
            }
            else
            {
                if (!Path.IsPathFullyQualified(_options.PrivateKeyPath))
                    throw new InvalidOperationException("Путь к закрытому JWT-ключу должен быть абсолютным.");
                _signingRsa.ImportFromPem(File.ReadAllText(_options.PrivateKeyPath));
                _ = _signingRsa.ExportParameters(true);
            }
            if (_signingRsa.KeySize < 3072)
                throw new InvalidOperationException("JWT требует RSA-ключ не короче 3072 бит.");
            if (string.IsNullOrWhiteSpace(_options.PublicKeyPath))
                _validationRsa.ImportSubjectPublicKeyInfo(_signingRsa.ExportSubjectPublicKeyInfo(), out _);
            else
            {
                if (!Path.IsPathFullyQualified(_options.PublicKeyPath) ||
                    string.Equals(Path.GetFullPath(_options.PrivateKeyPath!), Path.GetFullPath(_options.PublicKeyPath),
                        StringComparison.Ordinal))
                    throw new InvalidOperationException("Используйте отдельный абсолютный путь к открытому JWT-ключу.");
                var publicPem = File.ReadAllText(_options.PublicKeyPath);
                if (publicPem.Contains("PRIVATE KEY", StringComparison.Ordinal))
                    throw new InvalidOperationException("Файл открытого JWT-ключа не должен содержать закрытый ключ.");
                _validationRsa.ImportFromPem(publicPem);
                if (!CryptographicOperations.FixedTimeEquals(_signingRsa.ExportSubjectPublicKeyInfo(),
                        _validationRsa.ExportSubjectPublicKeyInfo()))
                    throw new InvalidOperationException("Открытый JWT-ключ не соответствует закрытому.");
            }
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(_signingRsa) { KeyId = _options.KeyId },
                SecurityAlgorithms.RsaSha256);
            ValidationKey = new RsaSecurityKey(_validationRsa) { KeyId = _options.KeyId };
        }
        catch
        {
            _signingRsa.Dispose();
            _validationRsa.Dispose();
            throw;
        }
    }

    public SigningCredentials SigningCredentials { get; }
    public SecurityKey ValidationKey { get; }

    public TokenValidationParameters ValidationParameters => new()
    {
        RequireSignedTokens = true, ValidateIssuerSigningKey = true, IssuerSigningKey = ValidationKey,
        TryAllIssuerSigningKeys = false, ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
        ValidateIssuer = true, ValidIssuer = _options.Issuer,
        ValidateAudience = true, ValidAudience = _options.Audience,
        RequireExpirationTime = true, ValidateLifetime = true, ClockSkew = TimeSpan.Zero,
        NameClaimType = SecurityTokenClaimTypes.UserName, RoleClaimType = SecurityTokenClaimTypes.AccountRole,
        LifetimeValidator = (notBefore, expires, _, _) => expires is not null &&
            expires.Value > _timeProvider.GetUtcNow().UtcDateTime &&
            (notBefore is null || notBefore.Value <= _timeProvider.GetUtcNow().UtcDateTime &&
                notBefore.Value < expires.Value)
    };

    public void Dispose()
    {
        _signingRsa.Dispose();
        _validationRsa.Dispose();
    }
}
