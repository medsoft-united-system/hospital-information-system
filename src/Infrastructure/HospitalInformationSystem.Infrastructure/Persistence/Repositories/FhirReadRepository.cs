using HospitalInformationSystem.Application.Abstractions;
using HospitalInformationSystem.Application.Fhir;
using HospitalInformationSystem.Domain.MedicalRecords;
using HospitalInformationSystem.Domain.MedicalWorkers;
using HospitalInformationSystem.Domain.Patients;
using HospitalInformationSystem.Infrastructure.Contexts;
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
        var query = db.LaboratoryAnalysisRequests.AsNoTracking();
        if (status is { } requestedStatus)
            query = query.Where(x => x.Status == requestedStatus);
        if (after is { } position)
            query = query.Where(x => EF.Functions.GreaterThan(
                ValueTuple.Create(EF.Property<DateTimeOffset>(x, "AuthoredAtFhirUtc"), x.Id),
                ValueTuple.Create(position.AtUtc, position.Id)));
        var rows = await query.OrderBy(x => EF.Property<DateTimeOffset>(x, "AuthoredAtFhirUtc"))
            .ThenBy(x => x.Id)
            .Select(x => new
            {
                Item = new FhirServiceRequestReadModel(x.Id, x.PatientId, x.RequestedByMedicalWorkerId,
                    x.LoincCodeSnapshot, x.DisplayNameSnapshot, x.Comment, x.AuthoredAtUtc, x.Status),
                PageAtUtc = EF.Property<DateTimeOffset>(x, "AuthoredAtFhirUtc")
            }).Take(count + 1).ToListAsync(cancellationToken);
        var items = rows.Take(count).Select(x => x.Item).ToArray();
        var next = rows.Count > count
            ? new FhirPagePosition(rows[count - 1].PageAtUtc, items[^1].Id) : null;
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
}
