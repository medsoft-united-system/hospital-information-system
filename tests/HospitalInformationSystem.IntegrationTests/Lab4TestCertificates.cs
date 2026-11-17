using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HospitalInformationSystem.Api.Hl7.Transport;

namespace HospitalInformationSystem.IntegrationTests;

internal sealed class Lab4TestCertificates : IDisposable
{
    private const string ServerPurpose = "1.3.6.1.5.5.7.3.1";
    private const string ClientPurpose = "1.3.6.1.5.5.7.3.2";
    private readonly string _directory = Directory.CreateTempSubdirectory("medsoft-l5-tls-").FullName;
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    public Lab4TestCertificates(string serverDnsName = "localhost")
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest("CN=MedSoft L5 ephemeral test CA", rootKey,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        rootRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootRequest.PublicKey, false));
        Root = rootRequest.CreateSelfSigned(_now.AddDays(-30), _now.AddDays(365));
        Server = Issue("CN=MedSoft L5 TLS server", ServerPurpose, _now.AddDays(-1), _now.AddDays(30), serverDnsName);
        Client = Issue("CN=Desktop certificate unrelated to account name", ClientPurpose,
            _now.AddDays(-1), _now.AddDays(30));

        PfxPath = Path.Combine(_directory, "server.pfx");
        PasswordFile = Path.Combine(_directory, "server-password.txt");
        CaCertificatePath = Path.Combine(_directory, "ca.pem");
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        File.WriteAllBytes(PfxPath, Server.Export(X509ContentType.Pfx, password));
        File.WriteAllText(PasswordFile, password);
        File.WriteAllText(CaCertificatePath, Root.ExportCertificatePem());
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(PfxPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.SetUnixFileMode(PasswordFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public X509Certificate2 Root { get; }
    public X509Certificate2 Server { get; }
    public X509Certificate2 Client { get; }
    public string PfxPath { get; }
    public string PasswordFile { get; }
    public string CaCertificatePath { get; }

    public Hl7ImportOptions Options()
    {
        var options = new Hl7ImportOptions { Enabled = true, ListenAddress = "127.0.0.1", Port = 0 };
        options.ServerCertificate.PfxPath = PfxPath;
        options.ServerCertificate.PasswordFile = PasswordFile;
        options.ClientTrust.CaCertificatePath = CaCertificatePath;
        options.Timeouts.HandshakeSeconds = 5;
        options.Timeouts.FrameReadSeconds = 5;
        options.Timeouts.ProcessingSeconds = 5;
        options.Timeouts.WriteSeconds = 5;
        options.Timeouts.ShutdownSeconds = 3;
        return options;
    }

    public X509Certificate2 ExpiredClient() =>
        Issue("CN=Expired L5 desktop", ClientPurpose, _now.AddDays(-2), _now.AddDays(-1));

    public X509Certificate2 WrongPurposeClient() =>
        Issue("CN=Server-only L5 certificate", ServerPurpose, _now.AddDays(-1), _now.AddDays(30));

    public bool ValidateServer(X509Certificate? certificate, X509Chain? suppliedChain, SslPolicyErrors errors)
    {
        if (certificate is null || (errors & (SslPolicyErrors.RemoteCertificateNameMismatch |
                                              SslPolicyErrors.RemoteCertificateNotAvailable)) != 0)
            return false;
        using var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(Root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(ServerPurpose));
        if (suppliedChain is not null)
            foreach (var element in suppliedChain.ChainElements.Cast<X509ChainElement>().Skip(1))
                chain.ChainPolicy.ExtraStore.Add(element.Certificate);
        return chain.Build(leaf);
    }

    private X509Certificate2 Issue(string subject, string purpose, DateTimeOffset from,
        DateTimeOffset until, string? dnsName = null)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid(purpose) }, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        if (dnsName is not null)
        {
            var alternativeNames = new SubjectAlternativeNameBuilder();
            alternativeNames.AddDnsName(dnsName);
            request.CertificateExtensions.Add(alternativeNames.Build());
        }
        using var certificate = request.Create(Root, from, until, RandomNumberGenerator.GetBytes(16));
        return certificate.CopyWithPrivateKey(key);
    }

    public void Dispose()
    {
        Client.Dispose();
        Server.Dispose();
        Root.Dispose();
        Directory.Delete(_directory, recursive: true);
    }
}
