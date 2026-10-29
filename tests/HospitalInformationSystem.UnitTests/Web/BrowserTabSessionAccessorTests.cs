using FluentAssertions;
using HospitalInformationSystem.Web.ApiClients;
using HospitalInformationSystem.Web.Authentication;
using Microsoft.JSInterop;
using Moq;

namespace HospitalInformationSystem.UnitTests.Web;

public sealed class BrowserTabSessionAccessorTests
{
    private const string TabId = "11223344556677889900aabbccddeeff";

    [Fact]
    public async Task GetId_ConcurrentRequestsWithOneCancellation_ShouldPreserveSharedInitialization()
    {
        var tabId = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var module = new Mock<IJSObjectReference>();
        module.Setup(reference => reference.InvokeAsync<string>("getTabId", It.IsAny<object?[]?>()))
            .Returns(new ValueTask<string>(tabId.Task));
        var js = Runtime(module.Object);
        await using var accessor = new BrowserTabSessionAccessor(js.Object);
        using var cancellation = new CancellationTokenSource();

        var cancelled = accessor.GetIdAsync(cancellation.Token);
        var surviving = accessor.GetIdAsync(CancellationToken.None);
        cancellation.Cancel();
        var action = async () => await cancelled;
        await action.Should().ThrowAsync<OperationCanceledException>();
        tabId.SetResult(TabId);

        (await surviving.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(TabId);
        (await accessor.GetIdAsync(CancellationToken.None)).Should().Be(TabId);
        js.Verify(runtime => runtime.InvokeAsync<IJSObjectReference>("import", It.IsAny<object?[]?>()), Times.Once);
        module.Verify(reference => reference.InvokeAsync<string>("getTabId", It.IsAny<object?[]?>()), Times.Once);
    }

    [Fact]
    public async Task Dispose_PendingImport_ShouldWaitAndDisposeImportedModuleOnce()
    {
        var imported = new TaskCompletionSource<IJSObjectReference>(TaskCreationOptions.RunContinuationsAsynchronously);
        var module = new Mock<IJSObjectReference>();
        module.Setup(reference => reference.InvokeAsync<string>("getTabId", It.IsAny<object?[]?>()))
            .ReturnsAsync(TabId);
        var js = new Mock<IJSRuntime>();
        js.Setup(runtime => runtime.InvokeAsync<IJSObjectReference>("import", It.IsAny<object?[]?>()))
            .Returns(new ValueTask<IJSObjectReference>(imported.Task));
        var accessor = new BrowserTabSessionAccessor(js.Object);
        var initializing = accessor.GetIdAsync(CancellationToken.None);

        var disposed = accessor.DisposeAsync().AsTask();

        disposed.IsCompleted.Should().BeFalse();
        imported.SetResult(module.Object);
        await Task.WhenAll(initializing, disposed).WaitAsync(TimeSpan.FromSeconds(5));
        await accessor.DisposeAsync();
        module.Verify(reference => reference.DisposeAsync(), Times.Once);
        var action = async () => await accessor.GetIdAsync(CancellationToken.None);
        await action.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task Dispose_PendingIdentifier_ShouldWaitBeforeReleasingModule()
    {
        var tabId = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var module = new Mock<IJSObjectReference>();
        module.Setup(reference => reference.InvokeAsync<string>("getTabId", It.IsAny<object?[]?>()))
            .Returns(new ValueTask<string>(tabId.Task));
        var accessor = new BrowserTabSessionAccessor(Runtime(module.Object).Object);
        var initializing = accessor.GetIdAsync(CancellationToken.None);

        var disposed = accessor.DisposeAsync().AsTask();

        disposed.IsCompleted.Should().BeFalse();
        module.Verify(reference => reference.DisposeAsync(), Times.Never);
        tabId.SetResult(TabId);
        await Task.WhenAll(initializing, disposed).WaitAsync(TimeSpan.FromSeconds(5));
        module.Verify(reference => reference.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task GetId_BrowserStorageUnavailable_ShouldReportFailureWithoutInventingSession()
    {
        var module = new Mock<IJSObjectReference>();
        module.Setup(reference => reference.InvokeAsync<string>("getTabId", It.IsAny<object?[]?>()))
            .ThrowsAsync(new JSException("Storage is unavailable."));
        await using var accessor = new BrowserTabSessionAccessor(Runtime(module.Object).Object);

        var action = async () => await accessor.GetIdAsync(CancellationToken.None);

        await action.Should().ThrowAsync<ApiException>().WithMessage("*сессию вкладки*");
    }

    [Fact]
    public async Task GetId_BrowserReturnsMalformedIdentifier_ShouldRejectBeforeCaching()
    {
        var module = new Mock<IJSObjectReference>();
        module.Setup(reference => reference.InvokeAsync<string>("getTabId", It.IsAny<object?[]?>()))
            .ReturnsAsync("untrusted-browser-value");
        await using var accessor = new BrowserTabSessionAccessor(Runtime(module.Object).Object);

        var action = async () => await accessor.GetIdAsync(CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>();
    }

    private static Mock<IJSRuntime> Runtime(IJSObjectReference module)
    {
        var js = new Mock<IJSRuntime>();
        js.Setup(runtime => runtime.InvokeAsync<IJSObjectReference>("import", It.IsAny<object?[]?>()))
            .ReturnsAsync(module);
        return js;
    }
}
