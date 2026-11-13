using System.Text.Json;
using HospitalInformationSystem.Contracts.LaboratoryTests;
using HospitalInformationSystem.Contracts.MedicalRecords;
using HospitalInformationSystem.Contracts.Patients;

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
            ("101363-0", "Чума: ДНК Yersinia pestis в крови (амплификация)"),
            ("6690-2", "Лейкоциты в крови (автоматический подсчёт)"),
            ("789-8", "Эритроциты в крови (автоматический подсчёт)"),
            ("4544-3", "Гематокрит крови (объёмная доля, автоматический подсчёт)"),
            ("787-2", "Средний объём эритроцита (MCV, автоматический подсчёт)"),
            ("785-6", "Среднее содержание гемоглобина в эритроците (MCH, автоматический подсчёт)"),
            ("786-4", "Средняя концентрация гемоглобина в эритроцитах (MCHC, автоматический подсчёт)"),
            ("788-0", "Ширина распределения эритроцитов по объёму (RDW-CV, автоматический подсчёт)"),
            ("777-3", "Тромбоциты в крови (автоматический подсчёт)"),
            ("32623-1", "Средний объём тромбоцита (MPV, автоматический подсчёт)"),
            ("4537-7", "Скорость оседания эритроцитов (СОЭ, метод Вестергрена)"),
            ("1988-5", "С-реактивный белок (массовая концентрация) в сыворотке или плазме"),
            ("4548-4", "Гликированный гемоглобин HbA1c / общий гемоглобин в крови (массовая доля)"),
            ("3016-3", "Тиреотропный гормон (ТТГ) в сыворотке или плазме"),
            ("3024-7", "Тироксин свободный (Т4, массовая концентрация) в сыворотке или плазме"),
            ("3051-0", "Трийодтиронин свободный (Т3, массовая концентрация) в сыворотке или плазме"),
            ("2160-0", "Креатинин (массовая концентрация) в сыворотке или плазме"),
            ("3094-0", "Азот мочевины (массовая концентрация) в сыворотке или плазме"),
            ("3084-1", "Мочевая кислота (массовая концентрация) в сыворотке или плазме"),
            ("1920-8", "Аспартатаминотрансфераза (АСТ, ферментативная активность) в сыворотке или плазме"),
            ("1742-6", "Аланинаминотрансфераза (АЛТ, ферментативная активность) в сыворотке или плазме"),
            ("1975-2", "Билирубин общий (массовая концентрация) в сыворотке или плазме"),
            ("1968-7", "Билирубин прямой (массовая концентрация) в сыворотке или плазме"),
            ("6768-6", "Щелочная фосфатаза (ферментативная активность) в сыворотке или плазме"),
            ("2324-2", "Гамма-глутамилтрансфераза (ГГТ, ферментативная активность) в сыворотке или плазме"),
            ("1751-7", "Альбумин (массовая концентрация) в сыворотке или плазме"),
            ("2885-2", "Белок общий (массовая концентрация) в сыворотке или плазме"),
            ("2093-3", "Холестерин общий (массовая концентрация) в сыворотке или плазме"),
            ("2085-9", "Холестерин ЛПВП (массовая концентрация) в сыворотке или плазме"),
            ("2571-8", "Триглицериды (массовая концентрация) в сыворотке или плазме"),
            ("2951-2", "Натрий (молярная концентрация) в сыворотке или плазме"),
            ("2823-3", "Калий (молярная концентрация) в сыворотке или плазме"),
            ("2075-0", "Хлориды (молярная концентрация) в сыворотке или плазме"),
            ("17861-6", "Кальций (массовая концентрация) в сыворотке или плазме")
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
