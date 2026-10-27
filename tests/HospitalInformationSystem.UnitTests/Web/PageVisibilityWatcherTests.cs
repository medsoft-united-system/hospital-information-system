using System.Reflection;
using FluentAssertions;
using HospitalInformationSystem.Web.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Moq;

namespace HospitalInformationSystem.UnitTests.Web;

public sealed class PageVisibilityWatcherTests
{
    [Fact]
    public async Task Notify_DisposedWatcher_ShouldIgnoreLateCallback()
    {
        var notifications = 0;
        var watcher = new PageVisibilityWatcher();
        typeof(PageVisibilityWatcher).GetProperty(nameof(PageVisibilityWatcher.OnVisible))!
            .SetValue(watcher, EventCallback.Factory.Create(new object(), () => notifications++));

        await watcher.OnVisibilityChangedAsync();
        await watcher.DisposeAsync();
        await watcher.OnVisibilityChangedAsync();

        notifications.Should().Be(1);
    }

    [Fact]
    public async Task Dispose_PendingImport_ShouldWaitAndReleaseModuleWithoutSubscribing()
    {
        var pending = new TaskCompletionSource<IJSObjectReference>(TaskCreationOptions.RunContinuationsAsynchronously);
        var module = new Mock<IJSObjectReference>();
        var js = new Mock<IJSRuntime>();
        js.Setup(runtime => runtime.InvokeAsync<IJSObjectReference>("import", It.IsAny<object?[]?>()))
            .Returns(new ValueTask<IJSObjectReference>(pending.Task));
        var watcher = CreateWatcher(js.Object);
        var initialized = Initialize(watcher);

        var disposed = watcher.DisposeAsync().AsTask();
        disposed.IsCompleted.Should().BeFalse();
        pending.SetResult(module.Object);
        await Task.WhenAll(initialized, disposed).WaitAsync(TimeSpan.FromSeconds(5));

        module.Verify(reference => reference.DisposeAsync(), Times.Once);
        module.Invocations.Should().NotContain(call => call.Method.Name == "InvokeAsync");
    }

    [Fact]
    public async Task Dispose_PendingSubscription_ShouldUnsubscribeWhenRegistrationCompletes()
    {
        var pending = new TaskCompletionSource<IJSObjectReference>(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscription = new Mock<IJSObjectReference>();
        var module = new Mock<IJSObjectReference>();
        module.Setup(reference => reference.InvokeAsync<IJSObjectReference>("subscribe", It.IsAny<object?[]?>()))
            .Returns(new ValueTask<IJSObjectReference>(pending.Task));
        var js = new Mock<IJSRuntime>();
        js.Setup(runtime => runtime.InvokeAsync<IJSObjectReference>("import", It.IsAny<object?[]?>()))
            .ReturnsAsync(module.Object);
        var watcher = CreateWatcher(js.Object);
        var initialized = Initialize(watcher);

        var disposed = watcher.DisposeAsync().AsTask();
        pending.SetResult(subscription.Object);
        await Task.WhenAll(initialized, disposed).WaitAsync(TimeSpan.FromSeconds(5));

        subscription.Invocations.Should().Contain(call => call.Method.Name == "InvokeAsync" &&
            Equals(call.Arguments[0], "dispose"));
        subscription.Verify(reference => reference.DisposeAsync(), Times.Once);
        module.Verify(reference => reference.DisposeAsync(), Times.Once);
    }

    private static PageVisibilityWatcher CreateWatcher(IJSRuntime js)
    {
        var watcher = new PageVisibilityWatcher();
        typeof(PageVisibilityWatcher).GetProperty("Js", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(watcher, js);
        return watcher;
    }

    private static Task Initialize(PageVisibilityWatcher watcher) => (Task)typeof(PageVisibilityWatcher)
        .GetMethod("OnAfterRenderAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(watcher, [true])!;
}
