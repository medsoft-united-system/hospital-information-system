using HospitalInformationSystem.Application.Abstractions;
using HospitalInformationSystem.Application.Fhir;
using HospitalInformationSystem.Domain.MedicalRecords;
using HospitalInformationSystem.Domain.MedicalWorkers;
using HospitalInformationSystem.Domain.Patients;
using HospitalInformationSystem.Infrastructure.Contexts;
using HospitalInformationSystem.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace HospitalInformationSystem.Infrastructure.Persistence.Repositories;

public sealed class FhirReadRepository(HospitalDbContext db) : IFhirReadRepository
{
    public Task<Patient?> GetPatientAsync(Guid id, CancellationToken cancellationToken) =>
        db.Patients.AsNoTracking().Where(x => x.Id == id).Select(x => new Patient
        {
            Id = x.Id, FirstName = x.FirstName, LastName = x.LastName, MiddleName = x.MiddleName,
            BirthDate = x.BirthDate, Sex = (AdministrativeSex)x.Sex
        }).SingleOrDefaultAsync(cancellationToken);

    public Task<MedicalWorker?> GetPractitionerAsync(Guid id, CancellationToken cancellationToken) =>
        db.MedicalWorkers.AsNoTracking().Where(x => x.Id == id).Select(x => new MedicalWorker
        {
            Id = x.Id, FirstName = x.FirstName, LastName = x.LastName, Position = x.Position,
            IsDeleted = x.IsDeleted
        }).SingleOrDefaultAsync(cancellationToken);

    public Task<FhirServiceRequestReadModel?> GetServiceRequestAsync(Guid id, CancellationToken cancellationToken) =>
        db.LaboratoryAnalysisRequests.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new FhirServiceRequestReadModel(x.Id, x.PatientId, x.RequestedByMedicalWorkerId,
                x.LoincCodeSnapshot, x.DisplayNameSnapshot, x.Comment, x.AuthoredAtUtc, x.Status))
            .SingleOrDefaultAsync(cancellationToken);

    public Task<FhirObservationReadModel?> GetObservationAsync(Guid id, CancellationToken cancellationToken) =>
        (from result in db.LaboratoryAnalysisResults.AsNoTracking().Where(x => x.Id == id)
         join request in db.LaboratoryAnalysisRequests.AsNoTracking()
             on result.LaboratoryAnalysisRequestId equals request.Id
         select new FhirObservationReadModel(result.Id, request.Id, result.PatientId,
             request.LoincCodeSnapshot, request.DisplayNameSnapshot, result.Value, result.IssuedAtUtc))
        .SingleOrDefaultAsync(cancellationToken);

    public async Task<FhirReadPage<FhirServiceRequestReadModel>> SearchServiceRequestsAsync(
        LaboratoryAnalysisRequestStatus? status, int count, FhirPagePosition? after,
        CancellationToken cancellationToken)
    {
        FhirReadValidation.RequirePage(count, after);
        FhirReadValidation.RequireStatus(status);
        var filtered = status.HasValue;
        var statusValue = status.HasValue ? LaboratoryAnalysisStorage.WriteStatus(status.Value) : string.Empty;
        var hasAfter = after is not null;
        var afterTime = after?.AtUtc ?? DateTimeOffset.UnixEpoch;
        var afterId = after?.Id ?? Guid.Empty;
        var take = count + 1;
        var rows = await db.Database.SqlQuery<ServiceRequestSearchRow>($"""
            SELECT id AS "Id", patient_id AS "PatientId",
                   requested_by_medical_worker_id AS "RequestedByMedicalWorkerId",
                   loinc_code_snapshot AS "LoincCode", display_name_snapshot AS "DisplayName",
                   comment AS "Comment", authored_at_utc AS "AuthoredAtUtc", status AS "Status"
            FROM laboratory_analysis_requests
            WHERE (NOT {filtered} OR status = {statusValue})
              AND (NOT {hasAfter}
                   OR date_trunc('milliseconds', authored_at_utc) > {afterTime}
                   OR (date_trunc('milliseconds', authored_at_utc) = {afterTime} AND id > {afterId}))
            ORDER BY date_trunc('milliseconds', authored_at_utc), id
            LIMIT {take}
            """).ToListAsync(cancellationToken);
        var items = rows.Take(count).Select(x => new FhirServiceRequestReadModel(x.Id, x.PatientId,
            x.RequestedByMedicalWorkerId, x.LoincCode, x.DisplayName, x.Comment, x.AuthoredAtUtc,
            LaboratoryAnalysisStorage.ReadStatus(x.Status))).ToArray();
        var next = rows.Count > count
            ? new FhirPagePosition(Milliseconds(items[^1].AuthoredAtUtc), items[^1].Id) : null;
        return new FhirReadPage<FhirServiceRequestReadModel>(items, next);
    }

    public async Task<FhirReadPage<FhirObservationReadModel>> SearchObservationsAsync(Guid basedOn, int count,
        FhirPagePosition? after, CancellationToken cancellationToken)
    {
        FhirReadValidation.RequireId(basedOn);
        FhirReadValidation.RequirePage(count, after);
        var query = db.LaboratoryAnalysisResults.AsNoTracking()
            .Where(x => x.LaboratoryAnalysisRequestId == basedOn);
        if (after is not null)
        {
            var position = after;
            query = query.Where(x => x.IssuedAtUtc > position.AtUtc ||
                                     x.IssuedAtUtc == position.AtUtc && x.Id.CompareTo(position.Id) > 0);
        }
        var rows = await (from result in query
            join request in db.LaboratoryAnalysisRequests.AsNoTracking()
                on result.LaboratoryAnalysisRequestId equals request.Id
            orderby result.IssuedAtUtc, result.Id
            select new FhirObservationReadModel(result.Id, request.Id, result.PatientId,
                request.LoincCodeSnapshot, request.DisplayNameSnapshot, result.Value, result.IssuedAtUtc))
            .Take(count + 1).ToListAsync(cancellationToken);
        var items = rows.Take(count).ToArray();
        var next = rows.Count > count ? new FhirPagePosition(items[^1].IssuedAtUtc, items[^1].Id) : null;
        return new FhirReadPage<FhirObservationReadModel>(items, next);
    }

    private static DateTimeOffset Milliseconds(DateTimeOffset value) =>
        value.ToUniversalTime().AddTicks(-(value.UtcTicks % TimeSpan.TicksPerMillisecond));

    private sealed class ServiceRequestSearchRow
    {
        public Guid Id { get; set; }
        public Guid PatientId { get; set; }
        public Guid RequestedByMedicalWorkerId { get; set; }
        public string LoincCode { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string? Comment { get; set; }
        public DateTimeOffset AuthoredAtUtc { get; set; }
        public string Status { get; set; } = string.Empty;
    }
}
