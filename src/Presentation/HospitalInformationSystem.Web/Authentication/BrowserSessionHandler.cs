using System.Net;
using System.Net.Http.Headers;
using HospitalInformationSystem.Contracts.Auth;
using HospitalInformationSystem.Contracts.Security;
using HospitalInformationSystem.Web.ApiClients;
using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace HospitalInformationSystem.Web.Authentication;

public sealed class BrowserSessionHandler(SessionState session, AntiforgeryTokenStore tokens,
    AuthenticationOperationCoordinator operations, Uri? apiOrigin = null) : DelegatingHandler
{
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private Uri? _origin = apiOrigin;
    private long _lastRefreshPreviousVersion = -1;
    private long _lastRefreshVersion = -1;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequireApiOrigin(request.RequestUri!);
        var version = session.Version;
        var originalSession = session.Current;
        using var retry = await CloneAsync(request, cancellationToken);
        RequireSessionVersion(version);
        Prepare(request);
        await AddAntiforgeryAsync(request, version, cancellationToken);
        RequireSessionVersion(version);
        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized || !CanRefresh(request.RequestUri!) ||
            originalSession is null && RequiresAntiforgery(request.Method))
        {
            return response;
        }

        long? refreshedVersion;
        try { refreshedVersion = await RefreshAsync(request.RequestUri!, version, originalSession, cancellationToken); }
        catch { response.Dispose(); throw; }
        if (refreshedVersion is null)
        {
            return response;
        }
        response.Dispose();
        using (retry)
        {
            var retryVersion = refreshedVersion.Value;
            RequireSessionVersion(retryVersion);
            Prepare(retry);
            await AddAntiforgeryAsync(retry, retryVersion, cancellationToken);
            RequireSessionVersion(retryVersion);
            var retried = await base.SendAsync(retry, cancellationToken);
            if (retried.StatusCode == HttpStatusCode.Unauthorized && session.Version == retryVersion)
            {
                tokens.Reset();
                session.SetSession(null);
            }
            return retried;
        }
    }

    private async Task<long?> RefreshAsync(Uri uri, long version, SessionDto? originalSession,
        CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        var operationStarted = false;
        try
        {
            using var operation = await operations.EnterAsync(cancellationToken);
            operationStarted = true;
            if (session.Version != version)
                return _lastRefreshPreviousVersion == version && _lastRefreshVersion == session.Version &&
                       session.AccessToken is not null ? session.Version : null;
            tokens.Reset();
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(uri, "/api/auth/refresh"));
            Prepare(request);
            await AddAntiforgeryAsync(request, version, cancellationToken);
            RequireSessionVersion(version);
            using var response = await base.SendAsync(request, cancellationToken);
            if (session.Version != version) return null;
            if (!response.IsSuccessStatusCode)
            {
                tokens.Reset();
                session.SetSession(null);
                return null;
            }
            var refreshed = await ApiResponseReader.ReadAsync<AuthenticationTokensDto>(response, cancellationToken);
            if (session.Version != version) return null;
            tokens.Reset();
            if (originalSession is not null && !HasSameIdentity(originalSession, refreshed.Session))
            {
                session.SetSession(null);
                return null;
            }
            session.SetTokens(refreshed);
            _lastRefreshPreviousVersion = version;
            _lastRefreshVersion = session.Version;
            return _lastRefreshVersion;
        }
        catch (ApiException)
        {
            if (session.Version == version)
            {
                tokens.Reset();
                session.SetSession(null);
            }
            return null;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            if (operationStarted && session.Version == version)
            {
                tokens.Reset();
                session.SetSession(null);
            }
            throw;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private void Prepare(HttpRequestMessage request)
    {
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        request.Headers.Authorization = session.AccessToken is { } access ? new AuthenticationHeaderValue("Bearer", access) : null;
        request.Headers.Remove("X-CSRF-TOKEN");
    }

    private async Task AddAntiforgeryAsync(HttpRequestMessage request, long sessionVersion,
        CancellationToken cancellationToken)
    {
        if (!RequiresAntiforgery(request.Method)) return;
        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            RequireSessionVersion(sessionVersion);
            var token = tokens.RequestToken;
            while (token is null)
            {
                var version = tokens.Version;
                using var csrf = new HttpRequestMessage(HttpMethod.Get, new Uri(request.RequestUri!, "/api/auth/csrf"));
                Prepare(csrf);
                using var response = await base.SendAsync(csrf, cancellationToken);
                var result = await ApiResponseReader.ReadAsync<AntiforgeryTokenDto>(response, cancellationToken);
                RequireSessionVersion(sessionVersion);
                if (tokens.Version == version) tokens.RequestToken = token = result.RequestToken;
            }
            request.Headers.Add("X-CSRF-TOKEN", token);
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private void RequireSessionVersion(long version)
    {
        if (session.Version != version)
            throw new ApiException("Сессия изменилась. Повторите операцию в текущей сессии.");
    }

    private static bool HasSameIdentity(SessionDto original, AuthenticationSessionDto refreshed) =>
        original.Id == refreshed.Id && original.Role == refreshed.AccountRole &&
        original.MedicalWorkerId == refreshed.MedicalWorkerId && original.MedicalWorkerRole == refreshed.MedicalWorkerRole;

    private void RequireApiOrigin(Uri request)
    {
        _origin ??= request;
        if (request.Scheme != _origin.Scheme || request.IdnHost != _origin.IdnHost || request.Port != _origin.Port)
            throw new InvalidOperationException("Учётные данные можно отправлять только настроенному API.");
    }

    private static bool CanRefresh(Uri uri) => !uri.AbsolutePath.StartsWith("/api/auth/", StringComparison.OrdinalIgnoreCase)
                                               || uri.AbsolutePath.Equals("/api/auth/session", StringComparison.OrdinalIgnoreCase);

    private static bool RequiresAntiforgery(HttpMethod method) =>
        method != HttpMethod.Get && method != HttpMethod.Head && method != HttpMethod.Options;

    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var copy = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version, VersionPolicy = request.VersionPolicy
        };
        foreach (var header in request.Headers) copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        foreach (var option in request.Options) copy.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
        if (request.Content is not null)
        {
            if (request.Content is MultipartContent multipart)
                foreach (var part in multipart) await part.LoadIntoBufferAsync(cancellationToken);
            copy.Content = new ByteArrayContent(await request.Content.ReadAsByteArrayAsync(cancellationToken));
            foreach (var header in request.Content.Headers) copy.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        return copy;
    }
}
