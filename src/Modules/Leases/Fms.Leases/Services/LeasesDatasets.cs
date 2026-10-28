using Fms.BuildingBlocks.Reporting;
using Fms.BuildingBlocks.Security;
using Fms.Leases.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Leases.Services;

public static class LeasesDatasets
{
    /// <summary>Leases by reference: titles, the other party and notes are left out.</summary>
    public static IEnumerable<IDatasetProvider> All(LeasesDbContext db)
    {
        yield return new Dataset<Domain.Lease>("Leases", Permissions.LeasesRead,
            [new("Id", "Edm.Guid"), new("Reference", "Edm.String"), new("Kind", "Edm.String"), new("Status", "Edm.String"), new("LocationId", "Edm.Guid"), new("StartOn", "Edm.String"), new("EndOn", "Edm.String"),
             new("CurrencyCode", "Edm.String"), new("BaseRent", "Edm.Decimal"), new("Frequency", "Edm.String"), new("CreatedAt", "Edm.DateTimeOffset")],
            () => db.Leases.AsNoTracking(), l => l.Id,
            l => DatasetRow.Of(("Id", l.Id), ("Reference", l.Reference), ("Kind", l.Kind.ToString()), ("Status", l.Status.ToString()), ("LocationId", l.LocationId), ("StartOn", l.StartOn.ToString("yyyy-MM-dd")),
                ("EndOn", l.EndOn.ToString("yyyy-MM-dd")), ("CurrencyCode", l.CurrencyCode), ("BaseRent", l.BaseRent), ("Frequency", l.Frequency.ToString()), ("CreatedAt", l.CreatedAt)),
            "CreatedAt", l => l.CreatedAt);
    }
}
