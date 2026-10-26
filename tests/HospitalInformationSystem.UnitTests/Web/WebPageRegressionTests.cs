using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using FluentAssertions;
using HospitalInformationSystem.Contracts.Auth;
using HospitalInformationSystem.Contracts.Diagnoses;
using HospitalInformationSystem.Contracts.MedicalRecords;
using HospitalInformationSystem.Contracts.MedicalWorkers;
using HospitalInformationSystem.Contracts.Patients;
using HospitalInformationSystem.Contracts.Security;
using HospitalInformationSystem.Web.ApiClients;
using HospitalInformationSystem.Web.Authentication;
using HospitalInformationSystem.Web.Gateways;
using HospitalInformationSystem.Web.Pages.Admin;
using HospitalInformationSystem.Web.Pages.Medical;

namespace HospitalInformationSystem.UnitTests.Web;

public sealed class WebPageRegressionTests
{
    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"text\"")]
    [InlineData("{\"text\":42}")]
    [InlineData("{\"text\":{}}")]
    public void ReadHistory_UnexpectedPayloadShape_ShouldKeepPageUsable(string payload)
    {
        var item = new MedicalRecordHistoryEventDto(1, DateTimeOffset.UtcNow, Guid.NewGuid(), "Доктор", "Врач",
            "PrescriptionAdded", null, payload);
        var describe = typeof(MedicalRecord).GetMethod("DescribeEvent", BindingFlags.Static | BindingFlags.NonPublic)!;

        var action = () => describe.Invoke(null, [item]);

        action.Should().NotThrow();
    }

    [Fact]
    public async Task RefreshPatients_FailedRefreshAfterDeletion_ShouldKeepPatientDeleted()
    {
        var patient = new PatientDto(Guid.NewGuid(), "Иван", "Иванов", null, new DateOnly(2000, 1, 1),
            AdministrativeSex.Male, null);
        using var http = CreateClient((request, _) => Task.FromResult(request.Method == HttpMethod.Delete
            ? new HttpResponseMessage(HttpStatusCode.NoContent)
            : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var page = new Patients();
        Inject(page, "Api", new PatientsApiClient(http));
        var state = new SessionState();
        state.SetSession(new SessionDto(Guid.NewGuid(), "doctor", ApplicationRoles.MedicalWorker,
            Guid.NewGuid(), MedicalWorkerRole.Doctor));
        Inject(page, "Session", state);
        Set(page, "_cts", new CancellationTokenSource());
        Set(page, "_patients", new[] { patient });
        Set(page, "_pendingDelete", patient);

        await InvokeAsync(page, "DeleteAsync");

        Get<IReadOnlyList<PatientDto>>(page, "_patients").Should().BeEmpty();
        Get<bool>(page, "_deleting").Should().BeFalse();
        Get<string>(page, "_error").Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData(MedicalWorkerRole.LaboratoryTechnician)]
    [InlineData(MedicalWorkerRole.Other)]
    public async Task DeletePatient_ReadOnlyMedicalRole_ShouldRejectDirectAction(MedicalWorkerRole role)
    {
        var state = new SessionState();
        state.SetSession(new SessionDto(Guid.NewGuid(), "reader", ApplicationRoles.MedicalWorker, Guid.NewGuid(), role));
        var patient = new PatientDto(Guid.NewGuid(), "Иван", "Иванов", null, new DateOnly(2000, 1, 1),
            AdministrativeSex.Male, null);
        using var http = CreateClient((_, _) => throw new InvalidOperationException("Read-only role must not send mutations."));
        using var page = new Patients();
        Inject(page, "Api", new PatientsApiClient(http));
        Inject(page, "Session", state);
        Set(page, "_cts", new CancellationTokenSource());

        Invoke(page, "AskDelete", patient);
        Get<PatientDto?>(page, "_pendingDelete").Should().BeNull();
        Set(page, "_pendingDelete", patient);
        await InvokeAsync(page, "DeleteAsync");

        Get<bool>(page, "_deleting").Should().BeFalse();
    }

    [Theory]
    [InlineData(MedicalWorkerRole.LaboratoryTechnician)]
    [InlineData(MedicalWorkerRole.Other)]
    public async Task SavePatient_ReadOnlyMedicalRole_ShouldKeepLoadedCardReadOnly(MedicalWorkerRole role)
    {
        var state = new SessionState();
        state.SetSession(new SessionDto(Guid.NewGuid(), "reader", ApplicationRoles.MedicalWorker, Guid.NewGuid(), role));
        var patient = new PatientDto(Guid.NewGuid(), "Иван", "Иванов", null, new DateOnly(2000, 1, 1),
            AdministrativeSex.Male, null);
        var reads = 0;
        using var http = CreateClient((request, _) =>
        {
            request.Method.Should().Be(HttpMethod.Get);
            reads++;
            return Task.FromResult(Json(patient));
        });
        using var page = new PatientEdit();
        typeof(PatientEdit).GetProperty(nameof(PatientEdit.Id))!.SetValue(page, patient.Id);
        Inject(page, "Api", new PatientsApiClient(http));
        Inject(page, "Session", state);

        await InvokeAsync(page, "LoadAsync");
        await InvokeAsync(page, "SaveAsync");
        await InvokeAsync(page, "DeleteAsync");

        reads.Should().Be(1);
        Get<object>(page, "_model").Should().NotBeNull();
        Get<bool>(page, "_busy").Should().BeFalse();
    }

    [Fact]
    public async Task RefreshDiagnoses_FailedRefreshAfterCreation_ShouldKeepCreatedDiagnosis()
    {
        var diagnosis = new DiagnosisDefinitionDto(Guid.NewGuid(), "A00.00", "Холера");
        using var http = CreateClient((request, _) => Task.FromResult(request.Method == HttpMethod.Post
            ? Json(diagnosis)
            : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var page = new Diagnoses();
        Inject(page, "Api", new DiagnosesApiClient(http));
        Set(page, "_cts", new CancellationTokenSource());
        Invoke(page, "BeginCreate");
        var form = Get<object>(page, "_form");
        form.GetType().GetProperty("Code")!.SetValue(form, diagnosis.Code);
        form.GetType().GetProperty("Name")!.SetValue(form, diagnosis.Name);

        await InvokeAsync(page, "SaveAsync");

        Get<IReadOnlyList<DiagnosisDefinitionDto>>(page, "_diagnoses").Should().ContainSingle().Which.Should().Be(diagnosis);
        Get<bool>(page, "_formOpen").Should().BeFalse();
        Get<string>(page, "_listError").Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ImportDiagnoses_ImportInProgress_ShouldPreventOverlappingEditsAndDeletes()
    {
        var diagnosis = new DiagnosisDefinitionDto(Guid.NewGuid(), "A00.00", "Холера");
        using var http = CreateClient((_, _) => throw new InvalidOperationException("No write is allowed during import."));
        using var page = new Diagnoses();
        Inject(page, "Api", new DiagnosesApiClient(http));
        Set(page, "_cts", new CancellationTokenSource());
        Invoke(page, "BeginEdit", diagnosis);
        var originalForm = Get<object>(page, "_form");
        Set(page, "_pendingDelete", diagnosis);
        Set(page, "_importing", true);

        Invoke(page, "BeginCreate");
        Invoke(page, "BeginEdit", diagnosis with { Name = "Изменённое название" });
        await InvokeAsync(page, "SaveAsync");
        await InvokeAsync(page, "DeleteAsync");

        Get<object>(page, "_form").Should().BeSameAs(originalForm);
        Get<Guid?>(page, "_editingId").Should().Be(diagnosis.Id);
    }

    [Fact]
    public async Task BeginEdit_DoctorSession_ShouldLoadDiagnosesWithoutRequestingMedicalWorkerDirectory()
    {
        var state = new SessionState();
        state.SetSession(new SessionDto(Guid.NewGuid(), "doctor", ApplicationRoles.MedicalWorker, Guid.NewGuid(),
            MedicalWorkerRole.Doctor));
        var requests = 0;
        using var http = CreateClient((request, _) =>
        {
            request.RequestUri!.AbsolutePath.Should().Be("/api/diagnoses");
            requests++;
            return Task.FromResult(Json(Array.Empty<DiagnosisDefinitionDto>()));
        });
        using var page = new MedicalRecord();
        Inject(page, "DiagnosesApi", new DiagnosesApiClient(http));
        Inject(page, "Session", state);
        Set(page, "_cts", new CancellationTokenSource());

        await InvokeAsync(page, "BeginEditAsync");

        requests.Should().Be(1);
        Get<IReadOnlyList<DiagnosisDefinitionDto>>(page, "_diagnoses").Should().BeEmpty();
        Get<bool>(page, "_editing").Should().BeTrue();
        Get<bool>(page, "_editDataLoading").Should().BeFalse();
    }

    [Fact]
    public async Task RefreshEditChoices_DeletedDiagnosis_ShouldClearSelection()
    {
        using var http = CreateClient((_, _) => Task.FromResult(Json(Array.Empty<object>())));
        using var page = new MedicalRecord();
        Inject(page, "DiagnosesApi", new DiagnosesApiClient(http));
        Inject(page, "Session", new SessionState());
        using var cancellation = new CancellationTokenSource();
        Set(page, "_selectedDiagnosisId", Guid.NewGuid());

        await InvokeAsync(page, "RefreshEditDataAsync", cancellation.Token);

        Get<Guid?>(page, "_selectedDiagnosisId").Should().BeNull();
    }

    [Fact]
    public async Task CreateRecord_NurseSession_ShouldCreateWithoutManualAuthorConfirmation()
    {
        var state = new SessionState();
        state.SetSession(new SessionDto(Guid.NewGuid(), "nurse", ApplicationRoles.MedicalWorker, Guid.NewGuid(),
            MedicalWorkerRole.Nurse));
        var patientId = Guid.NewGuid();
        var record = new MedicalRecordDto(Guid.NewGuid(), patientId, DateTimeOffset.UtcNow, 1, [], []);
        var mutations = 0;
        using var http = CreateClient((request, _) =>
        {
            request.Headers.Contains("X-Medical-Worker-Id").Should().BeFalse();
            if (request.Method == HttpMethod.Post) mutations++;
            return Task.FromResult(Json(record));
        });
        using var page = new MedicalRecord();
        typeof(MedicalRecord).GetProperty(nameof(MedicalRecord.PatientId))!.SetValue(page, patientId);
        Inject(page, "RecordsApi", new MedicalRecordsGateway(new MedicalRecordsApiClient(http), state));
        Inject(page, "Session", state);
        Set(page, "_cts", new CancellationTokenSource());

        await InvokeAsync(page, "CreateRecordAsync");

        mutations.Should().Be(1);
        Get<MedicalRecordDto>(page, "_record").Should().BeEquivalentTo(record);
        Get<bool>(page, "_busy").Should().BeFalse();
    }

    [Theory]
    [InlineData(MedicalWorkerRole.Nurse)]
    [InlineData(MedicalWorkerRole.LaboratoryTechnician)]
    [InlineData(MedicalWorkerRole.Other)]
    public async Task BeginEdit_NondoctorSession_ShouldKeepRecordReadOnly(MedicalWorkerRole role)
    {
        var state = new SessionState();
        state.SetSession(new SessionDto(Guid.NewGuid(), "worker", ApplicationRoles.MedicalWorker, Guid.NewGuid(), role));
        using var http = CreateClient((_, _) => throw new InvalidOperationException("Editing must remain unavailable."));
        using var page = new MedicalRecord();
        Inject(page, "RecordsApi", new MedicalRecordsGateway(new MedicalRecordsApiClient(http), state));
        Inject(page, "DiagnosesApi", new DiagnosesApiClient(http));
        Inject(page, "Session", state);
        Set(page, "_cts", new CancellationTokenSource());

        await InvokeAsync(page, "BeginEditAsync");
        await InvokeAsync(page, "RemoveDiagnosisAsync", Guid.NewGuid());

        Get<bool>(page, "_editing").Should().BeFalse();
        Get<bool>(page, "_busy").Should().BeFalse();
    }

    [Fact]
    public async Task RefreshAccounts_OlderResponse_ShouldPreserveNewlyCreatedAccount()
    {
        var staleResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var administrator = NewAccount("admin", ApplicationRoles.Administrator, null, false);
        var created = NewAccount("doctor", ApplicationRoles.MedicalWorker, Guid.NewGuid(), false);
        var accountRequests = 0;
        using var http = CreateClient((request, _) => request.RequestUri!.AbsolutePath == "/api/accounts"
            ? ++accountRequests == 1 ? staleResponse.Task : Task.FromResult(Json(new[] { administrator, created }))
            : Task.FromResult(Json(Array.Empty<MedicalWorkerDto>())));
        using var page = new Accounts();
        Inject(page, "Api", new AccountManagementGateway(http));
        Inject(page, "WorkersApi", new MedicalWorkersApiClient(http));

        var olderLoad = InvokeAsync(page, "LoadAsync");
        await InvokeAsync(page, "LoadAsync");
        staleResponse.SetResult(Json(new[] { administrator }));
        await olderLoad;

        Get<IReadOnlyList<AccountDetailsDto>>(page, "_accounts").Should().Equal(administrator, created);
        Get<bool>(page, "_loading").Should().BeFalse();
    }

    [Fact]
    public async Task CreateAccount_ListRefreshInProgress_ShouldBlockCreation()
    {
        using var http = CreateClient((_, _) => throw new InvalidOperationException("Creation during refresh is blocked."));
        using var page = new Accounts();
        Inject(page, "Api", new AccountManagementGateway(http));
        Set(page, "_loading", true);

        await InvokeAsync(page, "CreateAsync");

        Get<bool>(page, "_saving").Should().BeFalse();
    }

    [Theory]
    [InlineData(ApplicationRoles.Administrator, false, false, null, "Активен")]
    [InlineData(ApplicationRoles.Administrator, true, false, null, "Временно заблокирован")]
    [InlineData(ApplicationRoles.MedicalWorker, false, true, false, "Активен")]
    [InlineData(ApplicationRoles.MedicalWorker, false, true, true, "Сотрудник удалён")]
    [InlineData(ApplicationRoles.MedicalWorker, true, true, true, "Временно заблокирован")]
    [InlineData(ApplicationRoles.MedicalWorker, false, true, null, "Нет действующего сотрудника")]
    [InlineData(ApplicationRoles.MedicalWorker, false, false, false, "Нет действующего сотрудника")]
    [InlineData(ApplicationRoles.MedicalWorker, true, false, null, "Временно заблокирован")]
    public void GetAccountStatus_LockoutAndWorkerAvailability_ShouldReflectEffectiveAccess(
        string role, bool isLockedOut, bool hasWorkerLink, bool? workerDeleted, string expected)
    {
        var workerId = Guid.NewGuid();
        var account = NewAccount("account", role, hasWorkerLink ? workerId : null, isLockedOut);
        using var page = new Accounts();
        Set(page, "_workers", workerDeleted is { } isDeleted
            ? new[] { new MedicalWorkerDto(workerId, "Иван", "Иванов", "Врач", isDeleted) }
            : Array.Empty<MedicalWorkerDto>());

        var status = Invoke(page, "AccountStatus", account);

        status.Should().Be(expected);
    }

    [Fact]
    public void GetAccountStatus_InactiveAccount_ShouldShowAdministrativeDeactivation()
    {
        var account = NewAccount("account", ApplicationRoles.MedicalWorker, Guid.NewGuid(), false) with { IsActive = false };
        using var page = new Accounts();

        Invoke(page, "AccountStatus", account).Should().Be("Неактивен");
    }

    [Fact]
    public async Task CreateAccount_FailedRefreshAfterCreation_ShouldKeepGeneratedPasswordUntilDismissed()
    {
        var workerId = Guid.NewGuid();
        var account = NewAccount("doctor", ApplicationRoles.MedicalWorker, workerId, false);
        const string password = "Aa1Generated9";
        string? postedBody = null;
        using var http = CreateClient(async (request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                request.RequestUri!.AbsolutePath.Should().Be($"/api/medical-workers/{workerId}/account");
                postedBody = await request.Content!.ReadAsStringAsync();
                return Json(new CreateWorkerAccountResultDto(account, password));
            }
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });
        using var page = new Accounts();
        Inject(page, "Api", new AccountManagementGateway(http));
        Inject(page, "WorkersApi", new MedicalWorkersApiClient(http));
        var form = Get<object>(page, "_form");
        form.GetType().GetProperty("UserName")!.SetValue(form, "doctor");
        form.GetType().GetProperty("MedicalWorkerId")!.SetValue(form, workerId);

        await InvokeAsync(page, "CreateAsync");

        postedBody.Should().Be("{\"userName\":\"doctor\"}");
        Get<IReadOnlyList<AccountDetailsDto>>(page, "_accounts").Should().ContainSingle().Which.Should().Be(account);
        Get<string>(page, "_generatedPassword").Should().Be(password);
        Get<string>(page, "_listError").Should().NotBeNullOrWhiteSpace();
        Get<object>(page, "_form").GetType().GetProperty("Password").Should().BeNull();
        Invoke(page, "ClearGeneratedPassword");
        Get<string?>(page, "_generatedPassword").Should().BeNull();
        Get<string?>(page, "_generatedUserName").Should().BeNull();
    }

    [Fact]
    public async Task ResetPassword_ConfirmedAccount_ShouldKeepReturnedPasswordAndClearConfirmation()
    {
        var account = NewAccount("doctor", ApplicationRoles.MedicalWorker, Guid.NewGuid(), false);
        using var http = CreateClient((request, _) => Task.FromResult(request.Method == HttpMethod.Post
            ? Json(new ResetAccountPasswordResultDto(account.Id, "NewPassword1", 0))
            : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var page = new Accounts();
        Inject(page, "Api", new AccountManagementGateway(http));
        Inject(page, "WorkersApi", new MedicalWorkersApiClient(http));
        Invoke(page, "BeginReset", account);

        await InvokeAsync(page, "ResetPasswordAsync");

        Get<AccountDetailsDto?>(page, "_pendingReset").Should().BeNull();
        Get<string>(page, "_generatedPassword").Should().Be("NewPassword1");
        Get<string>(page, "_notice").Should().Contain("0");
        page.Dispose();
        Get<string?>(page, "_generatedPassword").Should().BeNull();
    }

    [Fact]
    public async Task LoadAccounts_DisposedBeforeResponse_ShouldFinishWithoutAccessingDisposedTokenSource()
    {
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = CreateClient((request, _) => request.RequestUri!.AbsolutePath == "/api/accounts"
            ? response.Task : Task.FromResult(Json(Array.Empty<MedicalWorkerDto>())));
        using var page = new Accounts();
        Inject(page, "Api", new AccountManagementGateway(http));
        Inject(page, "WorkersApi", new MedicalWorkersApiClient(http));
        var loading = InvokeAsync(page, "LoadAsync");
        page.Dispose();
        response.SetResult(Json(Array.Empty<AccountDetailsDto>()));

        var finish = async () => await loading;

        await finish.Should().NotThrowAsync();
    }

    private static AccountDetailsDto NewAccount(string userName, string role, Guid? workerId, bool lockedOut) =>
        new(Guid.NewGuid(), userName, role, workerId, null, null, true, lockedOut, DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);

    private static HttpClient CreateClient(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new ResponseHandler(send)) { BaseAddress = new Uri("http://localhost/") };

    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    private static void Set(object page, string field, object? value) =>
        page.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);
    private static T Get<T>(object page, string field) =>
        (T)page.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
    private static void Inject(object page, string property, object value) =>
        page.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);
    private static object? Invoke(object page, string method, params object[] arguments) =>
        page.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, arguments);
    private static Task InvokeAsync(object page, string method, params object[] arguments) =>
        (Task)Invoke(page, method, arguments)!;

    private sealed class ResponseHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
