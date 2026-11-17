using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using HospitalInformationSystem.Api.Hl7.Transport;

namespace HospitalInformationSystem.UnitTests;

public sealed class Lab4Hl7TlsConfigurationTests
{
    private const string ClientAuthentication = "1.3.6.1.5.5.7.3.2";
    private const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 34, 56, TimeSpan.Zero);

    [Fact]
    public void DisabledOptions_ShouldNotRequireTransportConfiguration()
    {
        var options = new Hl7ImportOptions
        {
            Enabled = false, Port = -1, ListenAddress = "invalid", ServerCertificate = null!,
            ClientTrust = null!, Timeouts = null!, Limits = null!
        };

        new Hl7ImportOptionsValidator().Validate(null, options).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void EnabledOptions_ShouldAcceptCompleteConfiguration()
    {
        using var pki = new TestPki();

        new Hl7ImportOptionsValidator().Validate(null, pki.Options).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("port-low")]
    [InlineData("port-high")]
    [InlineData("hostname")]
    [InlineData("handshake")]
    [InlineData("frame")]
    [InlineData("processing")]
    [InlineData("write")]
    [InlineData("shutdown")]
    [InlineData("payload")]
    [InlineData("connections")]
    [InlineData("backlog")]
    [InlineData("missing-server")]
    [InlineData("missing-password")]
    [InlineData("missing-ca")]
    [InlineData("null-server")]
    [InlineData("null-trust")]
    [InlineData("null-timeouts")]
    [InlineData("null-limits")]
    public void EnabledOptions_ShouldRejectInvalidConfiguration(string invalid)
    {
        using var pki = new TestPki();
        var options = pki.Options;
        switch (invalid)
        {
            case "port-low": options.Port = 0; break;
            case "port-high": options.Port = 65536; break;
            case "hostname": options.ListenAddress = "localhost"; break;
            case "handshake": options.Timeouts.HandshakeSeconds = 0; break;
            case "frame": options.Timeouts.FrameReadSeconds = 301; break;
            case "processing": options.Timeouts.ProcessingSeconds = 601; break;
            case "write": options.Timeouts.WriteSeconds = -1; break;
            case "shutdown": options.Timeouts.ShutdownSeconds = 121; break;
            case "payload": options.Limits.MaxPayloadBytes = 1048577; break;
            case "connections": options.Limits.MaxConnections = 257; break;
            case "backlog": options.Limits.Backlog = 0; break;
            case "missing-server": options.ServerCertificate.PfxPath = Path.Combine(pki.Directory, "absent"); break;
            case "missing-password": options.ServerCertificate.PasswordFile = ""; break;
            case "missing-ca": options.ClientTrust.CaCertificatePath = ""; break;
            case "null-server": options.ServerCertificate = null!; break;
            case "null-trust": options.ClientTrust = null!; break;
            case "null-timeouts": options.Timeouts = null!; break;
            case "null-limits": options.Limits = null!; break;
        }

        var result = new Hl7ImportOptionsValidator().Validate(null, options);

        result.Failed.Should().BeTrue();
        string.Join(" ", result.Failures).Should().NotContain(pki.Directory).And.NotContain(TestPki.Password);
    }

    [Fact]
    public void Load_ShouldOwnASeparateServerCertificateWithItsPrivateKey()
    {
        using var pki = new TestPki();
        using var material = Hl7TlsMaterial.Load(pki.Options, pki.Clock);

        material.ServerCertificate.HasPrivateKey.Should().BeTrue();
        material.ServerCertificate.Thumbprint.Should().Be(pki.Server.Thumbprint);
        material.ServerCertificate.Should().NotBeSameAs(pki.Server);
    }

    [Theory]
    [InlineData("no-private-key")]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("wrong-purpose")]
    [InlineData("missing-purpose")]
    [InlineData("ca-leaf")]
    [InlineData("missing-san")]
    [InlineData("wrong-key-usage")]
    public void Load_ShouldRejectInvalidServerCertificate(string invalid)
    {
        using var pki = new TestPki();
        using var replacement = invalid == "no-private-key"
            ? X509CertificateLoader.LoadCertificate(pki.Server.RawData)
            : pki.Issue(
                purpose: invalid == "missing-purpose" ? null :
                    invalid == "wrong-purpose" ? ClientAuthentication : ServerAuthentication,
                notBefore: invalid == "future" ? Now.AddDays(1) : Now.AddDays(-2),
                notAfter: invalid == "expired" ? Now.AddDays(-1) : Now.AddDays(2),
                authority: invalid == "ca-leaf",
                serverName: invalid != "missing-san",
                usage: invalid == "wrong-key-usage" ? X509KeyUsageFlags.KeyEncipherment :
                    invalid == "ca-leaf" ? X509KeyUsageFlags.KeyCertSign : X509KeyUsageFlags.DigitalSignature);
        pki.WriteServer(replacement);

        var load = () => Hl7TlsMaterial.Load(pki.Options, pki.Clock);

        load.Should().Throw<InvalidOperationException>().WithMessage("HL7 TLS certificate configuration is invalid.");
    }

    [Fact]
    public void Load_InvalidPassword_ShouldNotDisclosePasswordPathOrCryptographicError()
    {
        using var pki = new TestPki();
        File.WriteAllText(pki.Options.ServerCertificate.PasswordFile, "sensitive-wrong-password");

        var load = () => Hl7TlsMaterial.Load(pki.Options, pki.Clock);

        var exception = load.Should().Throw<InvalidOperationException>().Which;
        exception.ToString().Should().NotContain("sensitive-wrong-password").And.NotContain(pki.Directory);
        exception.InnerException.Should().BeNull();
    }

    [Theory]
    [InlineData("end-entity")]
    [InlineData("expired-authority")]
    [InlineData("missing-signing-usage")]
    [InlineData("private-key")]
    public void Load_ShouldRejectInvalidTrustMaterial(string invalid)
    {
        using var pki = new TestPki();
        using var replacement = invalid switch
        {
            "expired-authority" => pki.Issue(authority: true, notAfter: Now.AddDays(-1)),
            "missing-signing-usage" => pki.Issue(authority: true, usage: X509KeyUsageFlags.DigitalSignature),
            _ => pki.Issue()
        };
        var pem = invalid == "private-key"
            ? pki.Root.ExportCertificatePem() + "\n-----BEGIN PRIVATE KEY-----\ninvalid\n-----END PRIVATE KEY-----"
            : replacement.ExportCertificatePem();
        File.WriteAllText(pki.Options.ClientTrust.CaCertificatePath, pem);

        var load = () => Hl7TlsMaterial.Load(pki.Options, pki.Clock);

        load.Should().Throw<InvalidOperationException>().Which.InnerException.Should().BeNull();
    }

    [Fact]
    public void Callback_ShouldRejectMissingCertificate()
    {
        using var pki = new TestPki();
        using var material = Hl7TlsMaterial.Load(pki.Options, pki.Clock);

        material.ValidateClientCertificate(null, null, SslPolicyErrors.RemoteCertificateNotAvailable).Should().BeFalse();
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("wrong-purpose")]
    [InlineData("missing-purpose")]
    [InlineData("ca-leaf")]
    [InlineData("wrong-key-usage")]
    [InlineData("untrusted")]
    [InlineData("missing-flag")]
    public void Callback_ShouldRejectInvalidClientCertificate(string invalid)
    {
        using var pki = new TestPki();
        using var other = new TestPki();
        using var client = (invalid == "untrusted" ? other : pki).Issue(
            purpose: invalid == "missing-purpose" ? null :
                invalid == "wrong-purpose" ? ServerAuthentication : ClientAuthentication,
            notBefore: invalid == "future" ? Now.AddDays(1) : Now.AddDays(-2),
            notAfter: invalid == "expired" ? Now.AddDays(-1) : Now.AddDays(2),
            authority: invalid == "ca-leaf",
            usage: invalid == "wrong-key-usage" ? X509KeyUsageFlags.KeyEncipherment :
                invalid == "ca-leaf" ? X509KeyUsageFlags.KeyCertSign : X509KeyUsageFlags.DigitalSignature);
        using var material = Hl7TlsMaterial.Load(pki.Options, pki.Clock);

        material.ValidateClientCertificate(client, null, invalid == "missing-flag"
            ? SslPolicyErrors.RemoteCertificateNotAvailable : SslPolicyErrors.RemoteCertificateChainErrors)
            .Should().BeFalse();
    }

    [Fact]
    public void TlsOptions_ShouldRequireMutualTls13AndDisableDownloadsBeforeCallback()
    {
        using var pki = new TestPki();
        using var material = Hl7TlsMaterial.Load(pki.Options, pki.Clock);

        var options = material.CreateServerAuthenticationOptions();

        options.ServerCertificate.Should().BeSameAs(material.ServerCertificate);
        options.EnabledSslProtocols.Should().Be(SslProtocols.Tls13);
        options.ClientCertificateRequired.Should().BeTrue();
        options.AllowRenegotiation.Should().BeFalse();
        options.AllowTlsResume.Should().BeFalse();
        options.EncryptionPolicy.Should().Be(EncryptionPolicy.RequireEncryption);
        options.CertificateChainPolicy.Should().NotBeNull();
        var policy = options.CertificateChainPolicy!;
        policy.DisableCertificateDownloads.Should().BeTrue();
        policy.TrustMode.Should().Be(X509ChainTrustMode.CustomRootTrust);
        policy.CustomTrustStore.Cast<X509Certificate2>().Select(c => c.Thumbprint).Should().ContainSingle()
            .Which.Should().Be(pki.Root.Thumbprint);
        policy.RevocationMode.Should().Be(X509RevocationMode.NoCheck);
        policy.VerificationFlags.Should().Be(X509VerificationFlags.NoFlag);
        policy.ApplicationPolicy.Cast<Oid>().Select(oid => oid.Value).Should().Contain("1.3.6.1.5.5.7.3.2");
        options.RemoteCertificateValidationCallback.Should().NotBeNull();
        options.RemoteCertificateValidationCallback!(new object(), null, null,
            SslPolicyErrors.RemoteCertificateNotAvailable).Should().BeFalse();
    }

    [Fact]
    public void Callback_ShouldUseConfiguredCaAndPreserveActualCertificateMetadata()
    {
        using var pki = new TestPki();
        using var client = pki.Issue(subject: "CN=unrelated-client-name");
        using var material = Hl7TlsMaterial.Load(pki.Options, pki.Clock);

        material.ValidateClientCertificate(client, null, SslPolicyErrors.RemoteCertificateChainErrors).Should().BeTrue();
        var metadata = material.DescribeClient(client);

        metadata.Sha256Fingerprint.Should().Be(Convert.ToHexStringLower(SHA256.HashData(client.RawData)));
        metadata.Subject.Should().Be(client.Subject);
        metadata.Issuer.Should().Be(client.Issuer);
        metadata.SerialNumber.Should().Be(client.SerialNumber);
        metadata.NotBeforeUtc.Should().Be(new DateTimeOffset(client.NotBefore.ToUniversalTime()));
        metadata.NotAfterUtc.Should().Be(new DateTimeOffset(client.NotAfter.ToUniversalTime()));
    }

    [Fact]
    public void Callback_ShouldValidateIntermediateFromHandshakeAndDescribeVerifiedLeaf()
    {
        using var pki = new TestPki();
        using var intermediate = pki.Issue(authority: true, purpose: null, usage: X509KeyUsageFlags.KeyCertSign);
        using var client = pki.Issue(issuer: intermediate);
        using var supplied = new X509Chain();
        supplied.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        supplied.ChainPolicy.CustomTrustStore.Add(pki.Root);
        supplied.ChainPolicy.ExtraStore.Add(intermediate);
        supplied.ChainPolicy.VerificationTime = Now.UtcDateTime;
        supplied.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        supplied.ChainPolicy.DisableCertificateDownloads = true;
        supplied.Build(client).Should().BeTrue();
        using var material = Hl7TlsMaterial.Load(pki.Options, pki.Clock);

        material.ValidateClientCertificate(client, supplied, SslPolicyErrors.RemoteCertificateChainErrors).Should().BeTrue();
        material.DescribeClient(client).Subject.Should().Be(client.Subject);
    }

    [Fact]
    public void Callback_ShouldBuildThroughConfiguredIntermediate()
    {
        using var pki = new TestPki();
        using var intermediate = pki.Issue(authority: true, purpose: null, usage: X509KeyUsageFlags.KeyCertSign);
        using var client = pki.Issue(issuer: intermediate);
        File.WriteAllText(pki.Options.ClientTrust.CaCertificatePath,
            pki.Root.ExportCertificatePem() + "\n" + intermediate.ExportCertificatePem());
        using var material = Hl7TlsMaterial.Load(pki.Options, pki.Clock);

        material.ValidateClientCertificate(client, null, SslPolicyErrors.RemoteCertificateChainErrors).Should().BeTrue();
    }

    [Fact]
    public void Callback_ShouldRecheckCertificateLifetime()
    {
        using var pki = new TestPki();
        using var client = pki.Issue(notAfter: Now.AddMinutes(1));
        using var material = Hl7TlsMaterial.Load(pki.Options, pki.Clock);
        material.ValidateClientCertificate(client, null, SslPolicyErrors.None).Should().BeTrue();

        pki.Clock.Now = Now.AddMinutes(2);

        material.ValidateClientCertificate(client, null, SslPolicyErrors.None).Should().BeFalse();
    }

    [Fact]
    public void Dispose_ShouldLeaveCallerCertificatesAndChainUsable()
    {
        using var pki = new TestPki();
        using var client = pki.Issue();
        using var supplied = new X509Chain();
        supplied.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        supplied.ChainPolicy.CustomTrustStore.Add(pki.Root);
        supplied.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        supplied.ChainPolicy.VerificationTime = Now.UtcDateTime;
        supplied.Build(client).Should().BeTrue();
        var fingerprint = client.GetCertHashString(HashAlgorithmName.SHA256);
        var material = Hl7TlsMaterial.Load(pki.Options, pki.Clock);
        material.ValidateClientCertificate(client, supplied, SslPolicyErrors.None).Should().BeTrue();

        material.Dispose();
        material.Dispose();

        client.GetCertHashString(HashAlgorithmName.SHA256).Should().Be(fingerprint);
        supplied.ChainElements.Should().NotBeEmpty();
        pki.Server.HasPrivateKey.Should().BeTrue();
        pki.Root.HasPrivateKey.Should().BeTrue();
        material.ValidateClientCertificate(client, supplied, SslPolicyErrors.None).Should().BeFalse();
    }

    private sealed class FixedClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = Lab4Hl7TlsConfigurationTests.Now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class TestPki : IDisposable
    {
        public const string Password = "test-only-pfx-password.742!";
        private readonly List<X509Certificate2> _issued = [];
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "medsoft-l5-unit-" + Guid.NewGuid().ToString("N"));
        public FixedClock Clock { get; } = new();
        public X509Certificate2 Root { get; }
        public X509Certificate2 Server { get; }
        public Hl7ImportOptions Options { get; }

        public TestPki()
        {
            System.IO.Directory.CreateDirectory(Directory);
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = Request("CN=L5 unit root", key, null, true,
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, false);
            Root = request.CreateSelfSigned(Now.AddYears(-5), Now.AddYears(5));
            Server = Issue(purpose: ServerAuthentication, serverName: true);
            Options = new Hl7ImportOptions
            {
                Enabled = true,
                ServerCertificate = new Hl7ServerCertificateOptions
                {
                    PfxPath = Path.Combine(Directory, "server.pfx"),
                    PasswordFile = Path.Combine(Directory, "password.txt")
                },
                ClientTrust = new Hl7ClientTrustOptions { CaCertificatePath = Path.Combine(Directory, "ca.pem") }
            };
            WriteServer(Server);
            File.WriteAllText(Options.ServerCertificate.PasswordFile, Password + "\n");
            File.WriteAllText(Options.ClientTrust.CaCertificatePath, Root.ExportCertificatePem());
        }

        public X509Certificate2 Issue(string? purpose = ClientAuthentication, DateTimeOffset? notBefore = null,
            DateTimeOffset? notAfter = null, bool authority = false, bool serverName = false,
            X509KeyUsageFlags usage = X509KeyUsageFlags.DigitalSignature, X509Certificate2? issuer = null,
            string subject = "CN=L5 unit leaf")
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = Request(subject, key, purpose, authority, usage, serverName);
            using var publicCertificate = request.Create(issuer ?? Root, notBefore ?? Now.AddDays(-2),
                notAfter ?? Now.AddDays(2), RandomNumberGenerator.GetBytes(16));
            var certificate = publicCertificate.CopyWithPrivateKey(key);
            _issued.Add(certificate);
            return certificate;
        }

        public void WriteServer(X509Certificate2 certificate) =>
            File.WriteAllBytes(Options.ServerCertificate.PfxPath, certificate.Export(X509ContentType.Pfx, Password));

        public void Dispose()
        {
            foreach (var certificate in _issued) certificate.Dispose();
            Root.Dispose();
            System.IO.Directory.Delete(Directory, recursive: true);
        }

        private static CertificateRequest Request(string subject, ECDsa key, string? purpose,
            bool authority, X509KeyUsageFlags usage, bool serverName)
        {
            var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(authority, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(usage, true));
            if (purpose is not null)
                request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(purpose) }, false));
            if (serverName)
            {
                var names = new SubjectAlternativeNameBuilder();
                names.AddDnsName("localhost");
                request.CertificateExtensions.Add(names.Build());
            }
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            return request;
        }
    }
}
