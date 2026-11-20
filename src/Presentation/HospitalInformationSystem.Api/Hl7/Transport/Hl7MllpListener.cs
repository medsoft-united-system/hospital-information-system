using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;

namespace HospitalInformationSystem.Api.Hl7.Transport;

public sealed class Hl7MllpListener(
    IOptions<Hl7ImportOptions> configuredOptions,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<Hl7MllpListener> logger) : BackgroundService
{
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<long, TcpClient> _clients = new();
    private readonly ConcurrentDictionary<long, Task> _connections = new();
    private Hl7ImportOptions? _options;
    private Hl7TlsMaterial? _tls;
    private TcpListener? _listener;
    private SemaphoreSlim? _slots;
    private long _connectionSequence;
    private bool _stopping;
    private int _cleanupScheduled;

    public IPEndPoint? BoundEndpoint { get; private set; }
    public int ActiveConnectionCount => _clients.Count;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _options = configuredOptions.Value;
        if (!_options.Enabled) return base.StartAsync(cancellationToken);
        _tls = Hl7TlsMaterial.Load(_options, timeProvider);
        try
        {
            _slots = new SemaphoreSlim(_options.Limits.MaxConnections, _options.Limits.MaxConnections);
            _listener = new TcpListener(IPAddress.Parse(_options.ListenAddress), _options.Port);
            _listener.Start(_options.Limits.Backlog);
            BoundEndpoint = (IPEndPoint)_listener.LocalEndpoint;
            logger.LogInformation("HL7 MLLP listener started on {Address}:{Port}.",
                BoundEndpoint.Address, BoundEndpoint.Port);
            return base.StartAsync(cancellationToken);
        }
        catch
        {
            _listener?.Stop();
            _slots?.Dispose();
            _tls.Dispose();
            _tls = null;
            throw;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options is not { Enabled: true } || _listener is null) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (SocketException) when (IsStopping()) { break; }
            catch (ObjectDisposedException) when (IsStopping()) { break; }
            lock (_gate)
            {
                if (_stopping || stoppingToken.IsCancellationRequested)
                {
                    client.Dispose();
                    break;
                }
                if (!_slots!.Wait(0))
                {
                    client.Dispose();
                    logger.LogWarning("HL7 connection rejected: connection limit reached.");
                    continue;
                }
                var id = Interlocked.Increment(ref _connectionSequence);
                _clients.TryAdd(id, client);
                var task = HandleClientAsync(id, client, stoppingToken);
                _connections.TryAdd(id, task);
                _ = task.ContinueWith(completed =>
                {
                    _connections.TryRemove(id, out _);
                    _ = completed.Exception;
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }

    private bool IsStopping()
    {
        lock (_gate) return _stopping;
    }

    private async Task HandleClientAsync(long id, TcpClient client, CancellationToken stoppingToken)
    {
        try
        {
            client.NoDelay = true;
            var remoteAddress = client.Client.RemoteEndPoint?.ToString();
            using var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            using (var handshake = Deadline(stoppingToken, _options!.Timeouts.HandshakeSeconds))
            {
                await tls.AuthenticateAsServerAsync(_tls!.CreateServerAuthenticationOptions(), handshake.Token);
            }
            if (!tls.IsAuthenticated || !tls.IsMutuallyAuthenticated || !tls.IsEncrypted ||
                tls.SslProtocol != SslProtocols.Tls13 || tls.RemoteCertificate is null)
                throw new AuthenticationException("HL7 mutual TLS was not established.");
            using var peer = X509CertificateLoader.LoadCertificate(tls.RemoteCertificate.GetRawCertData());
            var certificateMetadata = _tls!.DescribeClient(peer);
            byte[] payload;
            using (var read = Deadline(stoppingToken, _options!.Timeouts.FrameReadSeconds))
                payload = await MllpFraming.ReadAsync(tls, _options.Limits.MaxPayloadBytes, read.Token);

            byte[]? acknowledgement;
            await using (var scope = scopeFactory.CreateAsyncScope())
            using (var processing = Deadline(stoppingToken, _options.Timeouts.ProcessingSeconds))
            using (processing.Token.Register(static state => ((TcpClient)state!).Dispose(), client))
            {
                acknowledgement = await scope.ServiceProvider.GetRequiredService<Hl7MessageProcessor>()
                    .ProcessAsync(payload, certificateMetadata, remoteAddress, processing.Token);
            }
            if (acknowledgement is not null)
            {
                using var write = Deadline(stoppingToken, _options.Timeouts.WriteSeconds);
                await MllpFraming.WriteAsync(tls, acknowledgement, write.Token);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (OperationCanceledException)
        {
            logger.LogWarning("HL7 connection {ConnectionId} closed: operation deadline reached.", id);
        }
        catch (AuthenticationException)
        {
            logger.LogWarning("HL7 connection {ConnectionId} rejected: TLS authentication failed.", id);
        }
        catch (MllpFrameException)
        {
            logger.LogWarning("HL7 connection {ConnectionId} closed: invalid MLLP frame.", id);
        }
        catch (IOException)
        {
            logger.LogWarning("HL7 connection {ConnectionId} closed: transport interrupted.", id);
        }
        catch (SocketException)
        {
            logger.LogWarning("HL7 connection {ConnectionId} closed: transport interrupted.", id);
        }
        catch (ObjectDisposedException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception)
        {
            logger.LogError("HL7 connection {ConnectionId} closed: processing failed.", id);
        }
        finally
        {
            client.Dispose();
            _clients.TryRemove(id, out _);
            _slots!.Release();
        }
    }

    private static CancellationTokenSource Deadline(CancellationToken stoppingToken, int seconds)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
        return deadline;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        var remaining = CloseConnections();
        using var deadline = Deadline(cancellationToken,
            _options is { Enabled: true } ? _options.Timeouts.ShutdownSeconds : 10);
        try
        {
            await base.StopAsync(deadline.Token);
            await remaining.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            logger.LogWarning("HL7 listener shutdown deadline reached.");
        }
        finally
        {
            ScheduleCleanup(remaining);
        }
    }

    private Task CloseConnections()
    {
        TcpClient[] clients;
        Task remaining;
        lock (_gate)
        {
            _stopping = true;
            _listener?.Stop();
            clients = _clients.Values.ToArray();
            remaining = Task.WhenAll(_connections.Values);
        }
        foreach (var client in clients) client.Dispose();
        return remaining;
    }

    private void ScheduleCleanup(Task remaining)
    {
        if (Interlocked.Exchange(ref _cleanupScheduled, 1) != 0) return;
        _ = remaining.ContinueWith(completed =>
        {
            _ = completed.Exception;
            _tls?.Dispose();
            _slots?.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public override void Dispose()
    {
        var remaining = CloseConnections();
        base.Dispose();
        ScheduleCleanup(remaining);
    }
}
