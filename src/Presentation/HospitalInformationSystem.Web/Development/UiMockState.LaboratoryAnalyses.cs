using System.Text.Json;
using HospitalInformationSystem.Models.LaboratoryTests;
using HospitalInformationSystem.Models.MedicalRecords;
using HospitalInformationSystem.Models.Patients;

namespace HospitalInformationSystem.Web.Development;

public sealed partial class UiMockState
{
    public static readonly Guid LaboratoryPatientId = Guid.Parse("20000000-0000-4000-8000-000000000003");
    public static readonly Guid LaboratoryMedicalRecordId = Guid.Parse("30000000-0000-4000-8000-000000000002");
    public static readonly Guid LaboratoryCompletedRequestId = Guid.Parse("60000000-0000-4000-8000-000000000001");
    public static readonly Guid LaboratoryActiveRequestId = Guid.Parse("60000000-0000-4000-8000-000000000002");
    public static readonly Guid LaboratoryResultId = Guid.Parse("70000000-0000-4000-8000-000000000001");
    public static readonly Guid LaboratoryServiceId = Guid.Parse("90000000-0000-4000-8000-000000000001");

    internal Dictionary<Guid, LaboratoryTestDefinitionDto> LaboratoryTests { get; } = [];

    private void SeedLaboratoryAnalyses(DateTimeOffset createdAt)
    {
        var definitions = new (string Code, string Name)[]
        {
            ("2345-7", "Глюкоза (массовая концентрация) в сыворотке или плазме"),
            ("718-7", "Гемоглобин (массовая концентрация) в крови"),
            ("7918-6", "ВИЧ-1/2: антитела в сыворотке"),
            ("5196-1", "Гепатит B: HBsAg в сыворотке или плазме (иммуноанализ)"),
            ("13955-0", "Гепатит C: антитела в сыворотке или плазме (иммуноанализ)"),
            ("5393-4", "Сифилис: антитела к Treponema pallidum в сыворотке (иммунофлуоресценция)"),
            ("101363-0", "Чума: ДНК Yersinia pestis в крови (амплификация)")
        };
        for (var index = 0; index < definitions.Length; index++)
        {
            var id = Guid.Parse($"80000000-0000-4000-8000-{index + 1:D12}");
            LaboratoryTests[id] = new LaboratoryTestDefinitionDto(id, definitions[index].Code,
                definitions[index].Name, true, createdAt, createdAt, 1);
        }

        Patients[LaboratoryPatientId] = new PatientDto(LaboratoryPatientId, "Андрей", "Лабораторный", "Учебный",
            new DateOnly(1992, 7, 24), AdministrativeSex.Male, "Синтетические анализы UiMock", true);
        var record = new MockMedicalRecord(new MedicalRecordDto(LaboratoryMedicalRecordId, LaboratoryPatientId,
            createdAt, 0, [], []));
        Records[LaboratoryMedicalRecordId] = record;
        AppendEvent(record, "MedicalRecordCreated", record.Current.Id,
            LaboratoryPayload(new { PatientId = LaboratoryPatientId }), createdAt);
        var glucose = LaboratoryTests.Values.Single(x => x.LoincCode == "2345-7");
        var hemoglobin = LaboratoryTests.Values.Single(x => x.LoincCode == "718-7");
        var completed = NewLaboratoryRequest(LaboratoryCompletedRequestId, glucose, createdAt.AddMinutes(1));
        var active = NewLaboratoryRequest(LaboratoryActiveRequestId, hemoglobin, createdAt.AddMinutes(2));
        AddSeedRequest(completed);
        AddSeedRequest(active);
        var received = createdAt.AddMinutes(3);
        var issued = received.AddSeconds(-5);
        issued = issued.AddTicks(-(issued.Ticks % TimeSpan.TicksPerMillisecond));
        var result = new LaboratoryAnalysisResultDto(LaboratoryResultId, completed.Id, LaboratoryPatientId,
            "5.6", issued, received, LaboratoryServiceId);
        record.Current = record.Current with
        {
            Version = record.Current.Version + 1,
            LaboratoryAnalysisRequests = record.Current.LaboratoryAnalysisRequests.Select(x => x.Id == completed.Id
                ? x with { Status = LaboratoryAnalysisRequestStatus.Completed, Result = result } : x).ToArray()
        };
        record.History.Add(new MedicalRecordHistoryEventDto(record.Current.Version, received, null, "lis.demo", "LIS",
            "LaboratoryAnalysisResultRecorded", result.Id, LaboratoryPayload(new
            {
                LaboratoryAnalysisRequestId = completed.Id, LaboratoryAnalysisResultId = result.Id,
                result.PatientId, completed.LoincCode, result.Value, result.IssuedAtUtc, result.ReceivedAtUtc,
                result.CreatedByServiceAccountId
            }))
        {
            AuthorKind = MedicalRecordAuthorKind.ServiceAccount, PerformedByServiceAccountId = LaboratoryServiceId
        });
        record.States.Add((received, record.Current));

        LaboratoryAnalysisRequestDto NewLaboratoryRequest(Guid id, LaboratoryTestDefinitionDto definition,
            DateTimeOffset at) => new(id, LaboratoryMedicalRecordId, LaboratoryPatientId, DoctorId, definition.Id,
            definition.LoincCode, definition.DisplayName, "Демонстрационный анализ", at,
            LaboratoryAnalysisRequestStatus.Active, null, $"{Workers[DoctorId].LastName} {Workers[DoctorId].FirstName}");

        void AddSeedRequest(LaboratoryAnalysisRequestDto request)
        {
            record.Current = record.Current with
            {
                LaboratoryAnalysisRequests = record.Current.LaboratoryAnalysisRequests.Append(request).ToArray()
            };
            AppendEvent(record, "LaboratoryAnalysisRequested", request.Id,
                LaboratoryPayload(new
                {
                    LaboratoryAnalysisRequestId = request.Id, request.MedicalRecordId, request.PatientId,
                    request.RequestedByMedicalWorkerId, request.LaboratoryTestDefinitionId, request.LoincCode,
                    request.DisplayName, request.Comment, request.AuthoredAtUtc
                }), request.AuthoredAtUtc);
        }
    }

    private static string LaboratoryPayload(object payload) =>
        JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
}
