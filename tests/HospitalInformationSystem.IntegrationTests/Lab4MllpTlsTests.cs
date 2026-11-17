using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using HospitalInformationSystem.Api.Hl7.Transport;
using HospitalInformationSystem.Application.Abstractions;
using HospitalInformationSystem.Contracts.ServiceAccounts;
using HospitalInformationSystem.Infrastructure.Contexts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace HospitalInformationSystem.IntegrationTests;

[Trait("Category", "MllpTls")]
public sealed class Lab4MllpTlsTests(ServiceAuthenticationApiFactory factory)
    : IClassFixture<ServiceAuthenticationApiFactory>
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private const string FixtureCredential = "lab4-fixture-access-token-not-valid";

    [Fact]
    public async Task AutomaticClientCertificateSelection_ShouldEstablishMutualTls()
    {
        using var certificates = new Lab4TestCertificates();
        var pair = await factory.IssueAsync((await CreateAccountAsync("HL7Import")).ApiKey);
        await using var server = await StartAsync(certificates.Options());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(server.Endpoint.Address, server.Endpoint.Port, timeout.Token);
        await using var tls = new SslStream(tcp.GetStream(), false,
            (_, certificate, chain, errors) => certificates.ValidateServer(certificate, chain, errors));
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "localhost", EnabledSslProtocols = SslProtocols.Tls13,
            ClientCertificates = new X509CertificateCollection { certificates.Client },
            AllowRenegotiation = false, AllowTlsResume = false,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck
        }, timeout.Token);

        await tls.WriteAsync(Frame(Payload("probe.hl7", pair.AccessToken, ControlId())), timeout.Token);
        var response = await ReadAckAsync(tls, timeout.Token);

        response.Code.Should().Be("AA");
        response.Text.Should().Be("CONNECTED");
        tls.IsMutuallyAuthenticated.Should().BeTrue();
    }

    [Fact]
    public async Task EnabledApiHost_ShouldStartRegisteredListenerAndStopItOnDisposal()
    {
        using var certificates = new Lab4TestCertificates();
        var pair = await factory.IssueAsync((await CreateAccountAsync("HL7Import")).ApiKey);
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var application = factory.Application.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Hl7Import:Enabled", "true");
            builder.UseSetting("Hl7Import:ListenAddress", "127.0.0.1");
            builder.UseSetting("Hl7Import:Port", port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.UseSetting("Hl7Import:ServerCertificate:PfxPath", certificates.PfxPath);
            builder.UseSetting("Hl7Import:ServerCertificate:PasswordFile", certificates.PasswordFile);
            builder.UseSetting("Hl7Import:ClientTrust:CaCertificatePath", certificates.CaCertificatePath);
        });
        Hl7MllpListener? listener = null;
        try
        {
            listener = application.Services.GetServices<IHostedService>().OfType<Hl7MllpListener>().Single();
            listener.BoundEndpoint.Should().NotBeNull();
            listener.BoundEndpoint!.Port.Should().Be(port);
            await using var scope = application.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            (await db.Database.GetAppliedMigrationsAsync()).Should().Equal(db.Database.GetMigrations());

            var response = await ExchangeAsync(listener.BoundEndpoint, certificates,
                Payload("probe.hl7", pair.AccessToken, ControlId()));

            response.Code.Should().Be("AA");
            response.Text.Should().Be("CONNECTED");
        }
        finally
        {
            await application.DisposeAsync();
        }
        listener.Should().NotBeNull();
        listener!.ActiveConnectionCount.Should().Be(0);
        using var closed = new TcpClient();
        var connect = async () => await closed.ConnectAsync(IPAddress.Loopback, port);
        await connect.Should().ThrowAsync<SocketException>();
    }

    [Fact]
    public async Task ValidMutualTlsAndJwt_ShouldCommitBeforeAckAndAuditActualClientCertificate()
    {
        using var certificates = new Lab4TestCertificates();
        var account = await CreateAccountAsync("HL7Import");
        var pair = await factory.IssueAsync(account.ApiKey);
        await using var server = await StartAsync(certificates.Options());
        var externalId = Guid.NewGuid().ToString("N");
        var controlId = ControlId();
        var payload = Payload("adt-multiple-diagnoses.hl7", pair.AccessToken, controlId, externalId);

        var acknowledgement = await ExchangeAsync(server.Endpoint, certificates, payload, fragmented: true);

        acknowledgement.Code.Should().Be("AA");
        acknowledgement.CorrelationId.Should().Be(controlId);
        acknowledgement.Header[8].Should().Be("ACK^A28^ACK");
        Guid.TryParseExact(acknowledgement.Text, "D", out var patientId).Should().BeTrue();
        patientId.Should().NotBeEmpty();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var patient = await db.Patients.AsNoTracking().SingleAsync(row => row.Id == patientId);
            patient.LastName.Should().Be("Кузнецов");
            patient.FirstName.Should().Be("Дмитрий");
            patient.MiddleName.Should().Be("Андреевич");
            var identifier = await db.PatientExternalIdentifiers.AsNoTracking()
                .SingleAsync(row => row.SourceSystem == "LAB4_XML" && row.ExternalPatientId == externalId);
            identifier.PatientId.Should().Be(patientId);
            var record = await db.MedicalRecords.AsNoTracking().SingleAsync(row => row.PatientId == patientId);
            var diagnoses = await db.MedicalRecordDiagnoses.AsNoTracking()
                .Where(row => row.MedicalRecordId == record.Id).ToArrayAsync();
            diagnoses.Should().HaveCount(2);
            diagnoses.Should().OnlyContain(row => row.AddedByServiceAccountId == account.Account.Id &&
                row.AddedByMedicalWorkerId == null && row.DiagnosedAtOffsetMinutes == 180);
            (await db.MedicalRecordHistoryEvents.CountAsync(row => row.MedicalRecordId == record.Id)).Should().Be(3);
            var audit = await db.AuditEvents.AsNoTracking().SingleAsync(row =>
                row.EventType == "Hl7PatientImported" && row.RequestId == controlId);
            audit.ActorServiceAccountId.Should().Be(account.Account.Id);
            audit.ActorDisplayName.Should().Be(account.Account.Name);
            using var metadata = JsonDocument.Parse(audit.MetadataJson);
            var json = metadata.RootElement;
            json.GetProperty("clientCertificateSha256").GetString()!.ToUpperInvariant()
                .Should().Be(Convert.ToHexString(SHA256.HashData(certificates.Client.RawData)));
            json.GetProperty("clientCertificateSubject").GetString().Should().Be(certificates.Client.Subject);
            json.GetProperty("clientCertificateIssuer").GetString().Should().Be(certificates.Client.Issuer);
            json.GetProperty("clientCertificateSerialNumber").GetString().Should().Be(certificates.Client.SerialNumber);
            json.GetProperty("clientCertificateNotBeforeUtc").GetDateTimeOffset()
                .Should().Be(new DateTimeOffset(certificates.Client.NotBefore.ToUniversalTime()));
            json.GetProperty("clientCertificateNotAfterUtc").GetDateTimeOffset()
                .Should().Be(new DateTimeOffset(certificates.Client.NotAfter.ToUniversalTime()));
            account.Account.Name.Should().NotBe(certificates.Client.GetNameInfo(X509NameType.SimpleName, false));
        }
        var beforeRetry = await ClinicalCountsAsync();
        var retryId = ControlId();

        var duplicate = await ExchangeAsync(server.Endpoint, certificates,
            Payload("adt-multiple-diagnoses.hl7", pair.AccessToken, retryId, externalId));

        duplicate.Code.Should().Be("AE");
        duplicate.CorrelationId.Should().Be(retryId);
        duplicate.Text.Should().StartWith("DUPLICATE:");
        (await ClinicalCountsAsync()).Should().Be(beforeRetry);
    }

    [Fact]
    public async Task AuthorizedProbe_ShouldAcknowledgeWithoutClinicalWrites()
    {
        using var certificates = new Lab4TestCertificates();
        var pair = await factory.IssueAsync((await CreateAccountAsync("HL7Import")).ApiKey);
        await using var server = await StartAsync(certificates.Options());
        var before = await ClinicalCountsAsync();

        var response = await ExchangeAsync(server.Endpoint, certificates, Payload("probe.hl7", pair.AccessToken, ControlId()));

        response.Code.Should().Be("AA");
        response.Text.Should().Be("CONNECTED");
        response.Header[8].Should().Be("ACK^N02^ACK");
        (await ClinicalCountsAsync()).Should().Be(before);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("foreign-ca")]
    [InlineData("expired")]
    [InlineData("wrong-eku")]
    public async Task InvalidClientCertificate_ShouldRefuseTlsWithoutImport(string kind)
    {
        using var certificates = new Lab4TestCertificates();
        using var foreign = kind == "foreign-ca" ? new Lab4TestCertificates() : null;
        using var custom = kind switch
        {
            "expired" => certificates.ExpiredClient(),
            "wrong-eku" => certificates.WrongPurposeClient(),
            _ => null
        };
        var certificate = kind switch
        {
            "missing" => null,
            "foreign-ca" => foreign!.Client,
            _ => custom
        };
        await using var server = await StartAsync(certificates.Options());
        var before = await ClinicalCountsAsync();

        await ExpectTlsRefusedAsync(server.Endpoint, certificates, certificate);

        (await ClinicalCountsAsync()).Should().Be(before);
    }

    [Fact]
    public async Task Tls12Client_ShouldBeRefusedByTls13Listener()
    {
        using var certificates = new Lab4TestCertificates();
        await using var server = await StartAsync(certificates.Options());

        await ExpectTlsRefusedAsync(server.Endpoint, certificates, certificates.Client, SslProtocols.Tls12);
    }

    [Fact]
    public async Task WrongServerHostname_ShouldBeRejectedByClient()
    {
        using var certificates = new Lab4TestCertificates("wrong-server.example");
        await using var server = await StartAsync(certificates.Options());

        var connect = async () =>
        {
            await using var client = await ConnectAsync(server.Endpoint, certificates, certificates.Client);
        };

        await connect.Should().ThrowAsync<AuthenticationException>();
    }

    [Theory]
    [InlineData("lis")]
    [InlineData("invalid")]
    [InlineData("revoked")]
    [InlineData("expired")]
    public async Task UnauthorizedJwtOverValidMutualTls_ShouldReturnAuthAckWithoutClinicalWrites(string kind)
    {
        using var certificates = new Lab4TestCertificates();
        var token = "deliberately-invalid-token";
        if (kind != "invalid")
        {
            var account = await CreateAccountAsync(kind == "lis" ? "LIS" : "HL7Import");
            token = (await factory.IssueAsync(account.ApiKey)).AccessToken;
            if (kind == "revoked")
            {
                await using var scope = factory.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
                await using var transaction = await db.Database.BeginTransactionAsync();
                var now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow();
                await scope.ServiceProvider.GetRequiredService<IServiceSessionRevoker>()
                    .RevokeAllAsync(account.Account.Id, now, "l5-tls-test", CancellationToken.None);
                await transaction.CommitAsync();
            }
            if (kind == "expired") factory.AdvanceTime(TimeSpan.FromMinutes(6));
        }
        await using var server = await StartAsync(certificates.Options());
        var before = await ClinicalCountsAsync();
        var controlId = ControlId();

        var response = await ExchangeAsync(server.Endpoint, certificates,
            Payload("adt-multiple-diagnoses.hl7", token, controlId, Guid.NewGuid().ToString("N")));

        response.Code.Should().Be("AR");
        response.CorrelationId.Should().Be(controlId);
        response.Text.Should().Be("AUTH: доступ запрещён");
        response.Errors.Should().ContainSingle();
        response.Errors[0][3].Should().Be("207^Application internal error^HL70357");
        response.Errors[0][4].Should().Be("E");
        response.Raw.Should().NotContain(token);
        (await ClinicalCountsAsync()).Should().Be(before);
    }

    [Theory]
    [InlineData("wrong-start")]
    [InlineData("nested-frame")]
    [InlineData("nul")]
    [InlineData("incomplete")]
    [InlineData("oversize")]
    public async Task MalformedFrame_ShouldCloseConnectionWithoutImport(string kind)
    {
        using var certificates = new Lab4TestCertificates();
        var options = certificates.Options();
        if (kind == "oversize") options.Limits.MaxPayloadBytes = 64;
        await using var server = await StartAsync(options);
        var before = await ClinicalCountsAsync();
        var bytes = kind switch
        {
            "wrong-start" => new byte[] { 0x01, 0x41, 0x1C, 0x0D },
            "nested-frame" => new byte[] { 0x0B, 0x41, 0x0B, 0x42, 0x1C, 0x0D },
            "nul" => new byte[] { 0x0B, 0x41, 0x00, 0x42, 0x1C, 0x0D },
            "incomplete" => new byte[] { 0x0B, 0x41, 0x1C },
            "oversize" => new byte[] { 0x0B }.Concat(Enumerable.Repeat((byte)'A', 65))
                .Concat(new byte[] { 0x1C, 0x0D }).ToArray(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        await using var client = await ConnectAsync(server.Endpoint, certificates, certificates.Client);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.Stream.WriteAsync(bytes, timeout.Token);
        await client.Stream.FlushAsync(timeout.Token);
        if (kind == "incomplete")
            await client.Stream.ShutdownAsync().WaitAsync(timeout.Token);

        await ExpectNoAckAsync(client.Stream);

        (await ClinicalCountsAsync()).Should().Be(before);
    }

    [Fact]
    public async Task HandshakeDeadline_ShouldCloseSilentTcpClient()
    {
        using var certificates = new Lab4TestCertificates();
        var options = certificates.Options();
        options.Timeouts.HandshakeSeconds = 1;
        await using var server = await StartAsync(options);
        using var client = new TcpClient();
        await client.ConnectAsync(server.Endpoint.Address, server.Endpoint.Port);
        await WaitUntilAsync(() => server.Listener.ActiveConnectionCount == 1);

        await ExpectNoAckAsync(client.GetStream());

        await WaitUntilAsync(() => server.Listener.ActiveConnectionCount == 0);
    }

    [Fact]
    public async Task FrameDeadline_ShouldRemainAbsoluteWhileBytesKeepArriving()
    {
        using var certificates = new Lab4TestCertificates();
        var options = certificates.Options();
        options.Timeouts.FrameReadSeconds = 1;
        await using var server = await StartAsync(options);
        await using var client = await ConnectAsync(server.Endpoint, certificates, certificates.Client);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.Stream.WriteAsync(new byte[] { 0x0B }, cancellation.Token);
        var closed = ExpectNoAckAsync(client.Stream, TimeSpan.FromSeconds(2));
        var drip = DripAsync(client.Stream, cancellation.Token);
        try
        {
            await closed;
        }
        finally
        {
            cancellation.Cancel();
            await drip;
        }
        await WaitUntilAsync(() => server.Listener.ActiveConnectionCount == 0);
    }

    [Fact]
    public async Task ProcessingDeadline_ShouldCancelBlockedImportWithoutPartialWrites()
    {
        using var certificates = new Lab4TestCertificates();
        var pair = await factory.IssueAsync((await CreateAccountAsync("HL7Import")).ApiKey);
        var options = certificates.Options();
        options.Timeouts.ProcessingSeconds = 1;
        await using var server = await StartAsync(options);
        var externalId = Guid.NewGuid().ToString("N");
        var before = await ClinicalCountsAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var lockKey = "hl7-patient:LAB4_XML:" + externalId;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))");
        await using var client = await ConnectAsync(server.Endpoint, certificates, certificates.Client);
        await client.Stream.WriteAsync(Frame(Payload("adt-multiple-diagnoses.hl7", pair.AccessToken,
            ControlId(), externalId)));

        await ExpectNoAckAsync(client.Stream);
        await WaitUntilAsync(() => server.Listener.ActiveConnectionCount == 0);
        await transaction.RollbackAsync();

        (await ClinicalCountsAsync()).Should().Be(before);
    }

    [Fact]
    public async Task SlowHandshake_ShouldLeaveCapacityForAnotherClientAndRejectExcessConnections()
    {
        using var certificates = new Lab4TestCertificates();
        var pair = await factory.IssueAsync((await CreateAccountAsync("HL7Import")).ApiKey);
        var options = certificates.Options();
        options.Limits.MaxConnections = 2;
        await using var server = await StartAsync(options);
        using var first = new TcpClient();
        await first.ConnectAsync(server.Endpoint.Address, server.Endpoint.Port);
        await WaitUntilAsync(() => server.Listener.ActiveConnectionCount == 1);

        var response = await ExchangeAsync(server.Endpoint, certificates,
            Payload("probe.hl7", pair.AccessToken, ControlId()));

        response.Text.Should().Be("CONNECTED");
        await WaitUntilAsync(() => server.Listener.ActiveConnectionCount == 1);
        using var second = new TcpClient();
        await second.ConnectAsync(server.Endpoint.Address, server.Endpoint.Port);
        await WaitUntilAsync(() => server.Listener.ActiveConnectionCount == 2);
        using var excess = new TcpClient();
        await excess.ConnectAsync(server.Endpoint.Address, server.Endpoint.Port);

        await ExpectNoAckAsync(excess.GetStream());
        server.Listener.ActiveConnectionCount.Should().Be(2);
    }

    [Fact]
    public async Task Stop_ShouldCloseActiveTlsConnectionsAndFinishWithinShutdownDeadline()
    {
        using var certificates = new Lab4TestCertificates();
        var options = certificates.Options();
        options.Timeouts.FrameReadSeconds = 30;
        options.Timeouts.ShutdownSeconds = 2;
        await using var server = await StartAsync(options);
        await using var client = await ConnectAsync(server.Endpoint, certificates, certificates.Client);
        await WaitUntilAsync(() => server.Listener.ActiveConnectionCount == 1);

        var stopwatch = Stopwatch.StartNew();
        await server.StopAsync().WaitAsync(TimeSpan.FromSeconds(4));

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(4));
        await ExpectNoAckAsync(client.Stream);
        server.Listener.ActiveConnectionCount.Should().Be(0);
    }

    private async Task<ServiceAccountKeyResultDto> CreateAccountAsync(string role)
    {
        using var administrator = await factory.AdministratorAsync();
        using var response = await administrator.PostAsJsonAsync("/api/service-accounts",
            new CreateServiceAccountRequest("l5-tls-" + Guid.NewGuid().ToString("N"), role));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ServiceAccountKeyResultDto>())!;
    }

    private async Task<Server> StartAsync(Hl7ImportOptions options)
    {
        OperatingSystem.IsLinux().Should().BeTrue("TLS 1.3 server tests run in the Linux MllpTls test profile");
        var listener = new Hl7MllpListener(Options.Create(options),
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            factory.Services.GetRequiredService<TimeProvider>(), NullLogger<Hl7MllpListener>.Instance);
        try
        {
            await listener.StartAsync(CancellationToken.None);
            listener.BoundEndpoint.Should().NotBeNull();
            return new Server(listener, listener.BoundEndpoint!);
        }
        catch
        {
            listener.Dispose();
            throw;
        }
    }

    private static async Task<Client> ConnectAsync(IPEndPoint endpoint, Lab4TestCertificates certificates,
        X509Certificate2? clientCertificate, string targetHost = "localhost",
        SslProtocols protocols = SslProtocols.Tls13)
    {
        var tcp = new TcpClient { NoDelay = true };
        SslStream? stream = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await tcp.ConnectAsync(endpoint.Address, endpoint.Port, timeout.Token);
            stream = new SslStream(tcp.GetStream(), false,
                (_, certificate, chain, errors) => certificates.ValidateServer(certificate, chain, errors));
            var supplied = new X509CertificateCollection();
            if (clientCertificate is not null) supplied.Add(clientCertificate);
            await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = targetHost, EnabledSslProtocols = protocols,
                ClientCertificates = supplied, AllowRenegotiation = false, AllowTlsResume = false,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                LocalCertificateSelectionCallback = (_, _, _, _, _) => clientCertificate
            }, timeout.Token);
            stream.SslProtocol.Should().Be(SslProtocols.Tls13);
            stream.IsEncrypted.Should().BeTrue();
            return new Client(tcp, stream);
        }
        catch
        {
            if (stream is not null) await stream.DisposeAsync();
            tcp.Dispose();
            throw;
        }
    }

    private static async Task<Ack> ExchangeAsync(IPEndPoint endpoint, Lab4TestCertificates certificates,
        byte[] payload, bool fragmented = false)
    {
        await using var client = await ConnectAsync(endpoint, certificates, certificates.Client);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var frame = Frame(payload);
        if (fragmented)
        {
            for (var offset = 0; offset < frame.Length; offset += 7)
            {
                await client.Stream.WriteAsync(frame.AsMemory(offset, Math.Min(7, frame.Length - offset)), timeout.Token);
                await client.Stream.FlushAsync(timeout.Token);
                await Task.Yield();
            }
        }
        else await client.Stream.WriteAsync(frame, timeout.Token);
        return await ReadAckAsync(client.Stream, timeout.Token);
    }

    private static async Task ExpectTlsRefusedAsync(IPEndPoint endpoint, Lab4TestCertificates certificates,
        X509Certificate2? clientCertificate, SslProtocols protocols = SslProtocols.Tls13)
    {
        try
        {
            await using var client = await ConnectAsync(endpoint, certificates, clientCertificate, protocols: protocols);
            await ExpectNoAckAsync(client.Stream);
        }
        catch (Exception exception) when (exception is AuthenticationException or IOException or SocketException)
        {
        }
    }

    private static async Task ExpectNoAckAsync(Stream stream, TimeSpan? deadline = null)
    {
        using var timeout = new CancellationTokenSource(deadline ?? TimeSpan.FromSeconds(4));
        var buffer = new byte[1];
        try
        {
            (await stream.ReadAsync(buffer, timeout.Token)).Should().Be(0, "the server closes this connection without an ACK");
        }
        catch (Exception exception) when (exception is AuthenticationException or IOException or SocketException)
        {
        }
    }

    private static async Task<Ack> ReadAckAsync(Stream stream, CancellationToken cancellationToken)
    {
        var one = new byte[1];
        (await stream.ReadAsync(one, cancellationToken)).Should().Be(1);
        one[0].Should().Be(0x0B);
        using var payload = new MemoryStream();
        while (true)
        {
            (await stream.ReadAsync(one, cancellationToken)).Should().Be(1);
            if (one[0] == 0x1C) break;
            payload.WriteByte(one[0]);
            payload.Length.Should().BeLessThan(8192);
        }
        (await stream.ReadAsync(one, cancellationToken)).Should().Be(1);
        one[0].Should().Be(0x0D);
        var text = Utf8.GetString(payload.ToArray());
        text.Should().StartWith("MSH|").And.EndWith("\r").And.NotContain("\n");
        var segments = text[..^1].Split('\r').Select(segment => segment.Split('|')).ToArray();
        var msa = segments.Should().ContainSingle(segment => segment[0] == "MSA").Subject;
        return new Ack(msa[1], msa[2], msa[3], segments[0],
            segments.Where(segment => segment[0] == "ERR").ToArray(), text);
    }

    private static async Task DripAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            for (var index = 0; index < 12; index++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                await stream.WriteAsync(new byte[] { (byte)'A' }, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or SocketException)
        {
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (!condition()) await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
    }

    private async Task<ClinicalCounts> ClinicalCountsAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        return new ClinicalCounts(await db.Patients.CountAsync(), await db.PatientExternalIdentifiers.CountAsync(),
            await db.MedicalRecords.CountAsync(), await db.MedicalRecordDiagnoses.CountAsync(),
            await db.DiagnosisDefinitions.CountAsync(), await db.MedicalRecordHistoryEvents.CountAsync());
    }

    private static byte[] Frame(byte[] payload) =>
        new byte[] { 0x0B }.Concat(payload).Concat(new byte[] { 0x1C, 0x0D }).ToArray();

    private static string ControlId() => "l5-" + Guid.NewGuid().ToString("N");

    private static byte[] Payload(string fixture, string token, string controlId, string? externalId = null)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Lab4", fixture);
        var text = Utf8.GetString(File.ReadAllBytes(path)).Replace(
            Convert.ToBase64String(Utf8.GetBytes(FixtureCredential)),
            Convert.ToBase64String(Utf8.GetBytes(token)), StringComparison.Ordinal);
        var segments = text[..^1].Split('\r');
        var msh = segments[0].Split('|');
        msh[9] = controlId;
        segments[0] = string.Join('|', msh);
        var pidIndex = Array.FindIndex(segments, segment => segment.StartsWith("PID|", StringComparison.Ordinal));
        if (externalId is not null && pidIndex >= 0)
        {
            var pid = segments[pidIndex].Split('|');
            pid[3] = externalId + "^^^LAB4_XML^PI";
            segments[pidIndex] = string.Join('|', pid);
        }
        return Utf8.GetBytes(string.Join('\r', segments) + '\r');
    }

    private sealed record Ack(string Code, string CorrelationId, string Text, string[] Header, string[][] Errors, string Raw);
    private sealed record ClinicalCounts(int Patients, int Identifiers, int Records, int Diagnoses, int Definitions, int History);

    private sealed class Client(TcpClient tcp, SslStream stream) : IAsyncDisposable
    {
        public SslStream Stream { get; } = stream;
        public async ValueTask DisposeAsync()
        {
            await Stream.DisposeAsync();
            tcp.Dispose();
        }
    }

    private sealed class Server(Hl7MllpListener listener, IPEndPoint endpoint) : IAsyncDisposable
    {
        private bool _stopped;
        public Hl7MllpListener Listener { get; } = listener;
        public IPEndPoint Endpoint { get; } = endpoint;

        public async Task StopAsync()
        {
            if (_stopped) return;
            _stopped = true;
            await Listener.StopAsync(CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            Listener.Dispose();
        }
    }
}
