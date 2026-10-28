using Fms.BuildingBlocks.Reporting;
using Fms.BuildingBlocks.Security;
using Fms.Energy.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Energy.Services;

public static class EnergyDatasets
{
    /// <summary>Meters and readings by code and number: names, notes and places typed by people are left out.</summary>
    public static IEnumerable<IDatasetProvider> All(EnergyDbContext db)
    {
        yield return new Dataset<Domain.EnergyMeter>("EnergyMeters", Permissions.EnergyRead,
            [new("Id", "Edm.Guid"), new("Code", "Edm.String"), new("Kind", "Edm.String"), new("Unit", "Edm.String"), new("LocationId", "Edm.Guid"), new("IsCumulative", "Edm.Boolean"), new("IsArchived", "Edm.Boolean")],
            () => db.Meters.AsNoTracking(), m => m.Id,
            m => DatasetRow.Of(("Id", m.Id), ("Code", m.Code), ("Kind", m.Kind.ToString()), ("Unit", m.Unit), ("LocationId", m.LocationId), ("IsCumulative", m.IsCumulative), ("IsArchived", m.IsArchived)));
        yield return new Dataset<Domain.EnergyReading>("EnergyReadings", Permissions.EnergyRead,
            [new("Id", "Edm.Guid"), new("MeterId", "Edm.Guid"), new("At", "Edm.DateTimeOffset"), new("Value", "Edm.Decimal"), new("Source", "Edm.String")],
            () => db.Readings.AsNoTracking().Where(r => !db.Voids.Any(v => v.ReadingId == r.Id)), r => r.Id,
            r => DatasetRow.Of(("Id", r.Id), ("MeterId", r.MeterId), ("At", r.At), ("Value", r.Value), ("Source", r.Source.ToString())),
            "At", r => r.At);
    }
}
