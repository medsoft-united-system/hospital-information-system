using System.Net;
using System.Reflection;
using FluentAssertions;
using HospitalInformationSystem.Application.Models.Auth;
using HospitalInformationSystem.Application.Models.MedicalRecords;
using HospitalInformationSystem.Application.Models.Patients;
using HospitalInformationSystem.Application.Models.Security;
using HospitalInformationSystem.Web.ApiClients;
using HospitalInformationSystem.Web.Authentication;
using HospitalInformationSystem.Web.Gateways;
using HospitalInformationSystem.Web.Pages.Medical;
using Moq;
using MedicalRecord = HospitalInformationSystem.Web.Pages.Medical.MedicalRecord;

namespace HospitalInformationSystem.UnitTests.Web;

public sealed class ClinicalPagePermissionTests
{
    [Theory]
    [InlineData(MedicalWorkerRole.Other)]
    [InlineData(MedicalWorkerRole.LaboratoryTechnician)]
    public async Task DeletePatient_RoleChangedAfterConfirmation_ShouldNotSendRequest(MedicalWorkerRole role)
    {
        var session = DoctorSession();
        var requests = 0;
        using var http = Client(() => requests++);
        using var page = new Patients();
        Inject(page, "Api", new PatientsApiClient(http));
        Inject(page, "Session", session);
        Set(page, "_cts", new CancellationTokenSource());
        Set(page, "_pendingDelete", new PatientDto(Guid.NewGuid(), "Иван", "Иванов", null,
            new DateOnly(2000, 1, 1), AdministrativeSex.Male, null));
        session.SetSession(session.Current! with { MedicalWorkerRole = role });

        await InvokeAsync(page, "DeleteAsync");

        requests.Should().Be(0);
    }

    [Fact]
    public async Task DeletePatientCard_RoleChangedAfterConfirmation_ShouldNotDeleteOrNavigate()
    {
        var session = DoctorSession();
        var requests = 0;
        using var http = Client(() => requests++);
        using var page = new PatientEdit();
        Inject(page, "Api", new PatientsApiClient(http));
        Inject(page, "Session", session);
        typeof(PatientEdit).GetProperty(nameof(PatientEdit.Id))!.SetValue(page, Guid.NewGuid());
        Set(page, "_routeCancellation", new CancellationTokenSource());
        Set(page, "_confirmDelete", true);
        session.SetSession(session.Current! with { MedicalWorkerRole = MedicalWorkerRole.Other });

        await InvokeAsync(page, "DeleteAsync");

        requests.Should().Be(0);
    }

    [Fact]
    public async Task EditRecord_DoctorBecameNurse_ShouldHideEditorAndNotRunMutation()
    {
        var session = DoctorSession();
        var mutations = 0;
        var records = new Mock<IMedicalRecordsGateway>();
        records.Setup(client => client.FindForPatientAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MedicalRecordDto?)null);
        using var page = new MedicalRecord();
        Inject(page, "Records", records.Object);
        Inject(page, "Session", session);
        Set(page, "_cts", new CancellationTokenSource());
        Set(page, "_editing", true);
        session.SetSession(session.Current! with { MedicalWorkerRole = MedicalWorkerRole.Nurse });

        await InvokeAsync(page, "RunMutationAsync", (Func<CancellationToken, Task>)(_ =>
        {
            mutations++;
            return Task.CompletedTask;
        }), false);

        mutations.Should().Be(0);
        ((bool)typeof(MedicalRecord).GetProperty("IsEditingRecord", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(page)!).Should().BeFalse();
        records.VerifyNoOtherCalls();
    }

    private static SessionState DoctorSession()
    {
        var session = new SessionState();
        session.SetSession(new SessionDto(Guid.NewGuid(), "doctor", ApplicationRoles.MedicalWorker,
            Guid.NewGuid(), MedicalWorkerRole.Doctor));
        return session;
    }

    private static HttpClient Client(Action sent) => new(new ProbeHandler(sent))
    { BaseAddress = new Uri("https://localhost/") };

    private static void Set(object component, string field, object value) => component.GetType()
        .GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(component, value);
    private static void Inject(object component, string property, object value) => component.GetType()
        .GetProperty(property, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(component, value);
    private static Task InvokeAsync(object component, string method, params object[] arguments) =>
        (Task)component.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(component, arguments)!;

    private sealed class ProbeHandler(Action sent) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            sent();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }
}
