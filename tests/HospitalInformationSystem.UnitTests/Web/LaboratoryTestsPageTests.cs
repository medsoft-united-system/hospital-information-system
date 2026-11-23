using System.Net;
using System.Reflection;
using FluentAssertions;
using HospitalInformationSystem.Application.Models.LaboratoryTests;
using HospitalInformationSystem.Web.ApiClients;
using HospitalInformationSystem.Web.Forms;
using HospitalInformationSystem.Web.Gateways;
using HospitalInformationSystem.Web.Pages.Admin;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HospitalInformationSystem.UnitTests.Web;

public sealed class LaboratoryTestsPageTests
{
    [Fact]
    public async Task Successful_create_remains_visible_if_following_refresh_fails()
    {
        var saved = Definition();
        var gateway = new StubGateway
        {
            Save = (_, request, _) =>
            {
                request.LoincCode.Should().Be(saved.LoincCode);
                request.DisplayName.Should().Be(saved.DisplayName);
                request.Version.Should().BeNull();
                return Task.FromResult(saved);
            },
            List = (_, activeOnly) =>
            {
                activeOnly.Should().BeFalse();
                throw new ApiException("Список временно недоступен.", HttpStatusCode.ServiceUnavailable);
            }
        };
        using var page = Page(gateway);
        Invoke(page, "BeginCreate");
        Set(page, "_form", new LaboratoryTestForm { LoincCode = " " + saved.LoincCode + " ", DisplayName = " " + saved.DisplayName + " " });

        await InvokeAsync(page, "SaveAsync");

        Get<IReadOnlyList<LaboratoryTestDefinitionDto>>(page, "_tests").Should().Equal(saved);
        Get<bool>(page, "_formOpen").Should().BeFalse();
        Get<bool>(page, "_saving").Should().BeFalse();
        Get<string>(page, "_listError").Should().Contain("недоступен");
    }

    [Fact]
    public async Task Successful_deactivation_is_preserved_if_refresh_fails()
    {
        var active = Definition();
        var inactive = active with { IsActive = false, UpdatedAtUtc = active.UpdatedAtUtc.AddSeconds(1), Version = active.Version + 1 };
        var gateway = new StubGateway
        {
            ChangeActive = (id, isActive, version, _) =>
            {
                id.Should().Be(active.Id);
                isActive.Should().BeFalse();
                version.Should().Be(active.Version);
                return Task.FromResult(inactive);
            },
            List = (_, _) => throw new HttpRequestException()
        };
        using var page = Page(gateway);
        Set(page, "_tests", new[] { active });
        Invoke(page, "AskStatus", active);

        await InvokeAsync(page, "SetActiveAsync");

        Get<IReadOnlyList<LaboratoryTestDefinitionDto>>(page, "_tests").Should().Equal(inactive);
        Get<LaboratoryTestDefinitionDto?>(page, "_pendingStatus").Should().BeNull();
        Get<bool>(page, "_changingStatus").Should().BeFalse();
        Get<string>(page, "_listError").Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Repeated_clicks_cannot_save_twice_or_replace_in_flight_form()
    {
        var saved = Definition();
        var completion = new TaskCompletionSource<LaboratoryTestDefinitionDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        using var page = Page(new StubGateway
        {
            Save = (_, _, _) => { requests++; return completion.Task; },
            List = (_, _) => Task.FromResult<IReadOnlyList<LaboratoryTestDefinitionDto>>([saved])
        });
        Invoke(page, "BeginCreate");
        var form = Get<LaboratoryTestForm>(page, "_form");
        form.LoincCode = saved.LoincCode;
        form.DisplayName = saved.DisplayName;

        var first = InvokeAsync(page, "SaveAsync");
        await InvokeAsync(page, "SaveAsync");
        Invoke(page, "BeginEdit", saved);
        Invoke(page, "CancelForm");
        Invoke(page, "AskStatus", saved);

        requests.Should().Be(1);
        Get<LaboratoryTestForm>(page, "_form").Should().BeSameAs(form);
        Get<bool>(page, "_formOpen").Should().BeTrue();
        Get<LaboratoryTestDefinitionDto?>(page, "_pendingStatus").Should().BeNull();
        completion.SetResult(saved);
        await first;
        Get<bool>(page, "_saving").Should().BeFalse();
    }

    [Fact]
    public async Task An_old_list_response_cannot_overwrite_a_more_recent_response()
    {
        var old = Definition() with { DisplayName = "Старое название" };
        var updated = old with { DisplayName = "Новое название" };
        var stale = new TaskCompletionSource<IReadOnlyList<LaboratoryTestDefinitionDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        using var page = Page(new StubGateway
        {
            List = (_, _) => ++requests == 1 ? stale.Task : Task.FromResult<IReadOnlyList<LaboratoryTestDefinitionDto>>([updated])
        });

        var initial = InvokeAsync(page, "LoadAsync");
        await InvokeAsync(page, "LoadAsync");
        stale.SetResult([old]);
        await initial;

        Get<IReadOnlyList<LaboratoryTestDefinitionDto>>(page, "_tests").Should().Equal(updated);
    }

    [Fact]
    public async Task A_list_started_before_a_write_cannot_restore_the_old_status()
    {
        var old = Definition();
        var saved = old with { IsActive = false };
        var stale = new TaskCompletionSource<IReadOnlyList<LaboratoryTestDefinitionDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        using var page = Page(new StubGateway
        {
            List = (_, _) => ++requests == 1 ? stale.Task : Task.FromResult<IReadOnlyList<LaboratoryTestDefinitionDto>>([saved]),
            ChangeActive = (_, _, _, _) => Task.FromResult(saved)
        });
        Set(page, "_tests", new[] { old });
        var initial = InvokeAsync(page, "LoadAsync");
        Invoke(page, "AskStatus", old);
        await InvokeAsync(page, "SetActiveAsync");
        stale.SetResult([old]);
        await initial;

        Get<IReadOnlyList<LaboratoryTestDefinitionDto>>(page, "_tests").Should().Equal(saved);
    }

    [Fact]
    public async Task Disposed_page_cancels_requests_and_ignores_late_response()
    {
        var response = new TaskCompletionSource<IReadOnlyList<LaboratoryTestDefinitionDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken capturedToken = default;
        using var page = Page(new StubGateway { List = (token, _) => { capturedToken = token; return response.Task; } });
        var load = InvokeAsync(page, "LoadAsync");

        page.Dispose();
        response.SetResult([Definition()]);
        await load;

        capturedToken.IsCancellationRequested.Should().BeTrue();
        Get<IReadOnlyList<LaboratoryTestDefinitionDto>?>(page, "_tests").Should().BeNull();
    }

    [Fact]
    public async Task Rendered_catalog_includes_inactive_tests_and_second_precision_without_delete_action()
    {
        var active = Definition();
        var inactive = active with { Id = Guid.NewGuid(), LoincCode = "718-7", DisplayName = "Гемоглобин", IsActive = false };
        var gateway = new StubGateway
        {
            List = (_, activeOnly) =>
            {
                activeOnly.Should().BeFalse();
                return Task.FromResult<IReadOnlyList<LaboratoryTestDefinitionDto>>([active, inactive]);
            }
        };
        var services = new ServiceCollection().AddLogging().AddSingleton<ILaboratoryTestsGateway>(gateway);
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            WebUtility.HtmlDecode((await renderer.RenderComponentAsync<LaboratoryTests>(ParameterView.Empty)).ToHtmlString()));

        html.Should().Contain("2345-7").And.Contain("718-7").And.Contain("Гемоглобин").And.Contain("Неактивен")
            .And.Contain(active.CreatedAtUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss"))
            .And.Contain("Деактивировать").And.Contain("Активировать").And.NotContain("Удалить");
    }

    [Fact]
    public void Search_and_status_filter_can_find_inactive_test_by_name_or_code()
    {
        var active = Definition();
        var inactive = active with { Id = Guid.NewGuid(), LoincCode = "718-7", DisplayName = "Гемоглобин", IsActive = false };
        using var page = Page(new StubGateway());
        Set(page, "_tests", new[] { active, inactive });
        Set(page, "_search", " ГЕМОГЛОБИН ");
        Set(page, "_statusFilter", "inactive");

        Visible(page).Should().Equal(inactive);
        Set(page, "_search", "718-7");
        Visible(page).Should().Equal(inactive);
        Set(page, "_statusFilter", "active");
        Visible(page).Should().BeEmpty();
    }

    [Fact]
    public async Task Editing_sends_the_original_version_even_after_the_list_is_refreshed()
    {
        var original = Definition();
        var latest = original with { DisplayName = "Изменено другим администратором", Version = original.Version + 1 };
        using var page = Page(new StubGateway
        {
            List = (_, _) => Task.FromResult<IReadOnlyList<LaboratoryTestDefinitionDto>>([latest]),
            Save = (id, request, _) =>
            {
                id.Should().Be(original.Id);
                request.Version.Should().Be(original.Version);
                request.DisplayName.Should().Be("Моё название");
                throw new ApiException("Анализ уже изменён.", HttpStatusCode.Conflict);
            }
        });
        Invoke(page, "BeginEdit", original);
        Get<LaboratoryTestForm>(page, "_form").DisplayName = "Моё название";

        await InvokeAsync(page, "LoadAsync");
        await InvokeAsync(page, "SaveAsync");

        Get<bool>(page, "_formStale").Should().BeTrue();
        Get<LaboratoryTestForm>(page, "_form").Version.Should().Be(original.Version);
    }

    [Fact]
    public async Task Edit_conflict_preserves_input_and_blocks_retry_until_explicit_reload()
    {
        var original = Definition();
        var latest = original with { DisplayName = "Актуальное серверное название", Version = original.Version + 1 };
        var requests = 0;
        var reads = 0;
        using var page = Page(new StubGateway
        {
            List = (_, _) => Task.FromResult<IReadOnlyList<LaboratoryTestDefinitionDto>>([latest]),
            Get = (id, _) => { id.Should().Be(original.Id); reads++; return Task.FromResult(latest); },
            Save = (_, request, _) =>
            {
                requests++;
                if (requests == 1) throw new ApiException("Анализ уже изменён.", HttpStatusCode.Conflict);
                request.Version.Should().Be(latest.Version);
                request.DisplayName.Should().Be("Повторно внесённое название");
                return Task.FromResult(latest with { DisplayName = request.DisplayName, Version = latest.Version + 1 });
            }
        });
        Invoke(page, "BeginEdit", original);
        var entered = Get<LaboratoryTestForm>(page, "_form");
        entered.DisplayName = "Моё несохранённое название";

        await InvokeAsync(page, "SaveAsync");
        await InvokeAsync(page, "LoadAsync");
        await InvokeAsync(page, "SaveAsync");

        requests.Should().Be(1);
        reads.Should().Be(0);
        Get<bool>(page, "_formOpen").Should().BeTrue();
        Get<bool>(page, "_formStale").Should().BeTrue();
        Get<LaboratoryTestForm>(page, "_form").Should().BeSameAs(entered);
        entered.DisplayName.Should().Be("Моё несохранённое название");
        entered.Version.Should().Be(original.Version);

        await InvokeAsync(page, "ReloadEditingAsync");

        reads.Should().Be(1);
        Get<bool>(page, "_formStale").Should().BeFalse();
        var reloaded = Get<LaboratoryTestForm>(page, "_form");
        reloaded.DisplayName.Should().Be(latest.DisplayName);
        reloaded.Version.Should().Be(latest.Version);
        reloaded.DisplayName = "Повторно внесённое название";
        await InvokeAsync(page, "SaveAsync");
        requests.Should().Be(2);
    }

    [Fact]
    public async Task Failed_conflict_reload_preserves_the_entered_form_and_keeps_save_blocked()
    {
        var original = Definition();
        var writes = 0;
        using var page = Page(new StubGateway
        {
            Save = (_, _, _) => { writes++; throw new ApiException("Анализ уже изменён.", HttpStatusCode.Conflict); },
            Get = (_, _) => throw new HttpRequestException()
        });
        Invoke(page, "BeginEdit", original);
        var entered = Get<LaboratoryTestForm>(page, "_form");
        entered.DisplayName = "Несохранённая правка";
        await InvokeAsync(page, "SaveAsync");

        await InvokeAsync(page, "ReloadEditingAsync");
        await InvokeAsync(page, "SaveAsync");

        Get<LaboratoryTestForm>(page, "_form").Should().BeSameAs(entered);
        entered.DisplayName.Should().Be("Несохранённая правка");
        entered.Version.Should().Be(original.Version);
        Get<bool>(page, "_formStale").Should().BeTrue();
        Get<bool>(page, "_reloadingRecord").Should().BeFalse();
        writes.Should().Be(1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Status_conflict_requires_a_fresh_read_and_explicit_reconfirmation(bool alreadyChanged)
    {
        var original = Definition();
        var latest = original with { IsActive = !alreadyChanged, Version = original.Version + 1 };
        var writes = 0;
        using var page = Page(new StubGateway
        {
            Get = (_, _) => Task.FromResult(latest),
            List = (_, _) => Task.FromResult<IReadOnlyList<LaboratoryTestDefinitionDto>>([latest]),
            ChangeActive = (id, active, version, _) =>
            {
                id.Should().Be(original.Id);
                active.Should().BeFalse();
                writes++;
                if (writes == 1)
                {
                    version.Should().Be(original.Version);
                    throw new ApiException("Анализ уже изменён.", HttpStatusCode.Conflict);
                }
                version.Should().Be(latest.Version);
                return Task.FromResult(latest with { IsActive = false, Version = latest.Version + 1 });
            }
        });
        Invoke(page, "AskStatus", original);

        await InvokeAsync(page, "SetActiveAsync");
        await InvokeAsync(page, "SetActiveAsync");

        writes.Should().Be(1);
        Get<bool>(page, "_statusStale").Should().BeTrue();
        Get<LaboratoryTestDefinitionDto>(page, "_pendingStatus").Should().Be(original);

        await InvokeAsync(page, "ReloadPendingStatusAsync");

        writes.Should().Be(1);
        Get<bool>(page, "_statusStale").Should().BeFalse();
        Get<IReadOnlyList<LaboratoryTestDefinitionDto>>(page, "_tests").Should().Equal(latest);
        if (alreadyChanged)
        {
            Get<LaboratoryTestDefinitionDto?>(page, "_pendingStatus").Should().BeNull();
        }
        else
        {
            Get<LaboratoryTestDefinitionDto>(page, "_pendingStatus").Should().Be(latest);
            await InvokeAsync(page, "SetActiveAsync");
            writes.Should().Be(2);
        }
    }

    private static LaboratoryTests Page(ILaboratoryTestsGateway gateway)
    {
        var page = new LaboratoryTests();
        typeof(LaboratoryTests).GetProperty("Gateway", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, gateway);
        Set(page, "_cts", new CancellationTokenSource());
        return page;
    }

    private static LaboratoryTestDefinitionDto Definition() => new(Guid.NewGuid(), "2345-7", "Глюкоза в крови", true,
        new DateTimeOffset(2026, 11, 9, 9, 17, 43, TimeSpan.Zero), new DateTimeOffset(2026, 11, 9, 10, 18, 57, TimeSpan.Zero), 17);
    private static IEnumerable<LaboratoryTestDefinitionDto> Visible(LaboratoryTests page) =>
        (IEnumerable<LaboratoryTestDefinitionDto>)page.GetType().GetProperty("VisibleTests", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
    private static void Set(object page, string name, object? value) =>
        page.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);
    private static T Get<T>(object page, string name) =>
        (T)page.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
    private static object? Invoke(object page, string name, params object[] arguments) =>
        page.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, arguments);
    private static Task InvokeAsync(object page, string name, params object[] arguments) => (Task)Invoke(page, name, arguments)!;

    private sealed class StubGateway : ILaboratoryTestsGateway
    {
        public Func<CancellationToken, bool, Task<IReadOnlyList<LaboratoryTestDefinitionDto>>>? List { get; init; }
        public Func<Guid, CancellationToken, Task<LaboratoryTestDefinitionDto>>? Get { get; init; }
        public Func<Guid?, SaveLaboratoryTestRequest, CancellationToken, Task<LaboratoryTestDefinitionDto>>? Save { get; init; }
        public Func<Guid, bool, uint, CancellationToken, Task<LaboratoryTestDefinitionDto>>? ChangeActive { get; init; }
        public Task<IReadOnlyList<LaboratoryTestDefinitionDto>> ListAsync(CancellationToken cancellationToken, bool activeOnly = true) => List!(cancellationToken, activeOnly);
        public Task<LaboratoryTestDefinitionDto> GetAsync(Guid id, CancellationToken cancellationToken) => Get!(id, cancellationToken);
        public Task<LaboratoryTestDefinitionDto> SaveAsync(Guid? id, SaveLaboratoryTestRequest request, CancellationToken cancellationToken) => Save!(id, request, cancellationToken);
        public Task<LaboratoryTestDefinitionDto> SetActiveAsync(Guid id, bool isActive, uint version, CancellationToken cancellationToken) => ChangeActive!(id, isActive, version, cancellationToken);
    }
}
