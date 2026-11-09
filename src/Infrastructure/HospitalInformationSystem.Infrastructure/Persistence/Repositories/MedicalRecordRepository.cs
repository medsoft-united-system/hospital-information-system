using System.Data;
using System.Linq.Expressions;
using HospitalInformationSystem.Application.Abstractions;
using HospitalInformationSystem.Application.Common;
using HospitalInformationSystem.Domain.MedicalRecords;
using HospitalInformationSystem.Infrastructure.Contexts;
using HospitalInformationSystem.Infrastructure.Persistence.Mappings;
using HospitalInformationSystem.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;

namespace HospitalInformationSystem.Infrastructure.Persistence.Repositories;

public sealed class MedicalRecordRepository(HospitalDbContext db) : IMedicalRecordRepository, IMedicalRecordPresence
{
    private IQueryable<MedicalRecordDbo> AggregateQuery(bool tracking) =>
        (tracking ? db.MedicalRecords : db.MedicalRecords.AsNoTracking())
        .Include(x => x.Diagnoses).Include(x => x.Prescriptions).Include(x => x.HistoryEvents)
        .Include(x => x.LaboratoryAnalysisRequests).ThenInclude(x => x.Result)
        .AsSplitQuery();

    public Task<MedicalRecord?> FindAsync(Guid id, bool tracking, CancellationToken cancellationToken) =>
        ReadAsync(x => x.Id == id, tracking, cancellationToken);

    public Task<MedicalRecord?> FindByPatientAsync(Guid patientId, CancellationToken cancellationToken) =>
        ReadAsync(x => x.PatientId == patientId, tracking: false, cancellationToken);

    public Task<Guid?> FindRecordIdByLaboratoryAnalysisRequestAsync(Guid id, CancellationToken cancellationToken) =>
        db.LaboratoryAnalysisRequests.AsNoTracking().Where(x => x.Id == id)
            .Select(x => (Guid?)x.MedicalRecordId).SingleOrDefaultAsync(cancellationToken);

    private async Task<MedicalRecord?> ReadAsync(
        Expression<Func<MedicalRecordDbo, bool>> predicate, bool tracking, CancellationToken cancellationToken)
    {
        var query = AggregateQuery(tracking);
        if (db.Database.CurrentTransaction is not null)
            return (await query.AsSingleQuery().SingleOrDefaultAsync(predicate, cancellationToken))?.ToDomain();

        await using var transaction =
            await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var row = await query.SingleOrDefaultAsync(predicate, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return row?.ToDomain();
    }

    public void Add(MedicalRecord record) => db.MedicalRecords.Add(record.ToDbo());

    public Task UpdateAsync(MedicalRecord record, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var row = db.MedicalRecords.Local.SingleOrDefault(x => x.Id == record.Id)
                  ?? throw new InvalidOperationException(
                      "Перед изменением ЭМК загрузите её с tracking: true в том же Unit of Work.");

        row.Version = record.Version;
        var diagnoses = row.Diagnoses.ToDictionary(x => x.Id);
        foreach (var diagnosis in record.Diagnoses)
        {
            if (diagnoses.TryGetValue(diagnosis.Id, out var existing)) diagnosis.UpdateDbo(existing);
            else row.Diagnoses.Add(diagnosis.ToDbo());
        }

        var prescriptions = row.Prescriptions.ToDictionary(x => x.Id);
        foreach (var prescription in record.Prescriptions)
        {
            if (prescriptions.TryGetValue(prescription.Id, out var existing)) prescription.UpdateDbo(existing);
            else row.Prescriptions.Add(prescription.ToDbo());
        }

        var analyses = row.LaboratoryAnalysisRequests.ToDictionary(x => x.Id);
        foreach (var analysis in record.LaboratoryAnalysisRequests)
        {
            if (!analyses.TryGetValue(analysis.Id, out var existing))
            {
                row.LaboratoryAnalysisRequests.Add(analysis.ToDbo());
                continue;
            }

            existing.Status = analysis.Status;
            if (existing.Result is null)
            {
                if (analysis.Result is not null) existing.Result = analysis.Result.ToDbo();
            }
            else if (analysis.Result is null || !SameResult(existing.Result, analysis.Result))
                throw new InvalidOperationException("Сохранённый результат анализа нельзя изменить или удалить.");
        }

        var eventIds = row.HistoryEvents.Select(x => x.Id).ToHashSet();
        foreach (var historyEvent in record.HistoryEvents.Where(x => !eventIds.Contains(x.Id)))
            row.HistoryEvents.Add(historyEvent.ToDbo());
        return Task.CompletedTask;
    }

    private static bool SameResult(LaboratoryAnalysisResultDbo saved, LaboratoryAnalysisResult incoming) =>
        saved.Id == incoming.Id && saved.LaboratoryAnalysisRequestId == incoming.LaboratoryAnalysisRequestId &&
        saved.PatientId == incoming.PatientId && saved.Value == incoming.Value &&
        saved.IssuedAtUtc == incoming.IssuedAtUtc && saved.ReceivedAtUtc == incoming.ReceivedAtUtc &&
        saved.CreatedByServiceAccountId == incoming.CreatedByServiceAccountId;

    public Task<bool> ExistsForPatientAsync(Guid patientId, CancellationToken cancellationToken) =>
        db.MedicalRecords.AnyAsync(x => x.PatientId == patientId, cancellationToken);

    public async Task<IReadOnlySet<Guid>> GetPatientIdsWithRecordsAsync(
        IReadOnlyCollection<Guid> patientIds, CancellationToken cancellationToken) =>
        (await db.MedicalRecords.Where(x => patientIds.Contains(x.PatientId))
            .Select(x => x.PatientId).ToListAsync(cancellationToken)).ToHashSet();
}
