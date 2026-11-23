using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using HospitalInformationSystem.Models.Hl7Import;

namespace HospitalInformationSystem.Api.Hl7.Transport;

public sealed class Hl7TlsMaterial : IDisposable
{
    private const string ClientAuthentication = "1.3.6.1.5.5.7.3.2";
    private const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";
    private readonly X509Certificate2Collection _authorities;
    private readonly X509Certificate2Collection _roots;
    private readonly X509Certificate2Collection _intermediates;
    private readonly TimeProvider _timeProvider;
    private int _disposed;

    private Hl7TlsMaterial(X509Certificate2 serverCertificate, X509Certificate2Collection authorities,
        X509Certificate2Collection roots, X509Certificate2Collection intermediates, TimeProvider timeProvider)
    {
        ServerCertificate = serverCertificate;
        _authorities = authorities;
        _roots = roots;
        _intermediates = intermediates;
        _timeProvider = timeProvider;
    }

    public X509Certificate2 ServerCertificate { get; }

    public static Hl7TlsMaterial Load(Hl7ImportOptions options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (!options.Enabled)
            throw new InvalidOperationException("HL7 TLS is disabled.");
        if (options.ServerCertificate is null || options.ClientTrust is null ||
            string.IsNullOrWhiteSpace(options.ServerCertificate.PfxPath) ||
            string.IsNullOrWhiteSpace(options.ServerCertificate.PasswordFile) ||
            string.IsNullOrWhiteSpace(options.ClientTrust.CaCertificatePath))
            throw new InvalidOperationException("HL7 TLS configuration is invalid.");

        X509Certificate2? server = null;
        var authorities = new X509Certificate2Collection();
        try
        {
            var password = ReadText(options.ServerCertificate.PasswordFile, 16 * 1024).TrimEnd('\r', '\n');
            if (password.Length == 0 || password.Any(c => c is '\r' or '\n' or '\0'))
                throw new CryptographicException();
            RequireFileSize(options.ServerCertificate.PfxPath, 1024 * 1024);
            server = X509CertificateLoader.LoadPkcs12FromFile(options.ServerCertificate.PfxPath, password,
                OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet);
            var now = timeProvider.GetUtcNow();
            if (!server.HasPrivateKey || !ValidLeaf(server, ServerAuthentication, now) || !HasServerName(server))
                throw new CryptographicException();

            var pem = ReadText(options.ClientTrust.CaCertificatePath, 1024 * 1024);
            if (pem.Contains("PRIVATE KEY-----", StringComparison.Ordinal))
                throw new CryptographicException();
            authorities.ImportFromPem(pem);
            if (authorities.Count is 0 or > 32)
                throw new CryptographicException();
            var roots = new X509Certificate2Collection();
            var intermediates = new X509Certificate2Collection();
            foreach (var authority in authorities)
            {
                if (!IsAuthority(authority) || !ValidTime(authority, now) ||
                    !authority.Extensions.OfType<X509KeyUsageExtension>()
                        .Any(extension => extension.KeyUsages.HasFlag(X509KeyUsageFlags.KeyCertSign)))
                    throw new CryptographicException();
                if (authority.SubjectName.RawData.AsSpan().SequenceEqual(authority.IssuerName.RawData))
                    roots.Add(authority);
                else
                    intermediates.Add(authority);
            }
            if (roots.Count == 0) throw new CryptographicException();
            return new Hl7TlsMaterial(server, authorities, roots, intermediates, timeProvider);
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or
            UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            server?.Dispose();
            foreach (var authority in authorities) authority.Dispose();
            throw new InvalidOperationException("HL7 TLS certificate configuration is invalid.");
        }
    }

    public SslServerAuthenticationOptions CreateServerAuthenticationOptions()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return new SslServerAuthenticationOptions
        {
            ServerCertificate = ServerCertificate,
            ClientCertificateRequired = true,
            EnabledSslProtocols = SslProtocols.Tls13,
            AllowRenegotiation = false,
            AllowTlsResume = false,
            EncryptionPolicy = EncryptionPolicy.RequireEncryption,
            CertificateChainPolicy = CreateClientChainPolicy(),
            RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
                ValidateClientCertificate(certificate, chain, errors)
        };
    }

    private X509ChainPolicy CreateClientChainPolicy()
    {
        var policy = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            VerificationFlags = X509VerificationFlags.NoFlag,
            VerificationTime = _timeProvider.GetUtcNow().UtcDateTime,
            RevocationMode = X509RevocationMode.NoCheck,
            DisableCertificateDownloads = true
        };
        policy.CustomTrustStore.AddRange(_roots);
        policy.ExtraStore.AddRange(_intermediates);
        policy.ApplicationPolicy.Add(new Oid(ClientAuthentication));
        return policy;
    }

    public bool ValidateClientCertificate(X509Certificate? certificate, X509Chain? suppliedChain,
        SslPolicyErrors errors)
    {
        if (Volatile.Read(ref _disposed) != 0 || certificate is null ||
            errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
            return false;
        try
        {
            using var client = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            var now = _timeProvider.GetUtcNow();
            if (!ValidLeaf(client, ClientAuthentication, now)) return false;
            using var chain = new X509Chain { ChainPolicy = CreateClientChainPolicy() };
            if (suppliedChain is not null)
                foreach (var element in suppliedChain.ChainElements.Cast<X509ChainElement>().Skip(1))
                    chain.ChainPolicy.ExtraStore.Add(element.Certificate);
            return chain.Build(client);
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException or
            InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }

    public ImportClientCertificate DescribeClient(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        if (Volatile.Read(ref _disposed) != 0 || !ValidLeaf(certificate, ClientAuthentication, _timeProvider.GetUtcNow()))
            throw new InvalidOperationException("HL7 client certificate is invalid.");
        return new ImportClientCertificate(
            Convert.ToHexStringLower(certificate.GetCertHash(HashAlgorithmName.SHA256)),
            certificate.Subject, certificate.Issuer, certificate.SerialNumber,
            new DateTimeOffset(certificate.NotBefore.ToUniversalTime()),
            new DateTimeOffset(certificate.NotAfter.ToUniversalTime()));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        ServerCertificate.Dispose();
        foreach (var authority in _authorities) authority.Dispose();
    }

    private static bool ValidLeaf(X509Certificate2 certificate, string purpose, DateTimeOffset now) =>
        !IsAuthority(certificate) && ValidTime(certificate, now) &&
        certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>()
            .Any(extension => extension.EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == purpose)) &&
        certificate.Extensions.OfType<X509KeyUsageExtension>()
            .All(extension => extension.KeyUsages.HasFlag(X509KeyUsageFlags.DigitalSignature));

    private static bool IsAuthority(X509Certificate2 certificate) =>
        certificate.Extensions.OfType<X509BasicConstraintsExtension>().Any(extension => extension.CertificateAuthority);

    private static bool ValidTime(X509Certificate2 certificate, DateTimeOffset now) =>
        certificate.NotBefore.ToUniversalTime() <= now.UtcDateTime &&
        now.UtcDateTime <= certificate.NotAfter.ToUniversalTime();

    private static bool HasServerName(X509Certificate2 certificate)
    {
        var extension = certificate.Extensions["2.5.29.17"];
        if (extension is null) return false;
        var names = new X509SubjectAlternativeNameExtension(extension.RawData, extension.Critical);
        return names.EnumerateDnsNames().Any() || names.EnumerateIPAddresses().Any();
    }

    private static string ReadText(string path, long maximumBytes)
    {
        RequireFileSize(path, maximumBytes);
        return File.ReadAllText(path, new UTF8Encoding(false, true));
    }

    private static void RequireFileSize(string path, long maximumBytes)
    {
        var size = new FileInfo(path).Length;
        if (size <= 0 || size > maximumBytes) throw new IOException();
    }
}
