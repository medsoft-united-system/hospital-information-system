using HospitalInformationSystem.Models.Auth;
using HospitalInformationSystem.Web.ApiClients;
using Microsoft.JSInterop;

namespace HospitalInformationSystem.Web.Authentication;

public sealed class BrowserTabSessionAccessor(IJSRuntime js) : IBrowserTabSession, IAsyncDisposable
{
    private Task<string>? _initialization;
    private IJSObjectReference? _module;
    private bool _disposed;

    public Task<string> GetIdAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        return (_initialization ??= InitializeAsync()).WaitAsync(cancellationToken);
    }

    private async Task<string> InitializeAsync()
    {
        try
        {
            _module = await js.InvokeAsync<IJSObjectReference>("import", "./js/tab-session.js");
            var id = await _module.InvokeAsync<string>("getTabId");
            if (!BrowserTabSession.IsValid(id))
                throw new InvalidOperationException("Браузер вернул некорректный идентификатор вкладки.");
            return id;
        }
        catch (JSException)
        {
            throw new ApiException("Не удалось подготовить сессию вкладки. Разрешите хранилище сайта и обновите страницу.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_initialization is not null)
                await _initialization;
        }
        catch (Exception exception) when (exception is ApiException or InvalidOperationException)
        {
        }
        finally
        {
            if (_module is not null)
                await _module.DisposeAsync();
        }
    }
}
