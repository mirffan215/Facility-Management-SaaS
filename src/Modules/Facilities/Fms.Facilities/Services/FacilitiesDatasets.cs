using Fms.BuildingBlocks.Reporting;
using Fms.BuildingBlocks.Security;
using Fms.Facilities.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Facilities.Services;

public static class FacilitiesDatasets
{
    public static IEnumerable<IDatasetProvider> All(FacilitiesDbContext db)
    {
        yield return new Dataset<Domain.Asset>("Assets", Permissions.AssetsRead,
            [new("Id", "Edm.Guid"), new("Code", "Edm.String"), new("Name", "Edm.String"), new("AssetClassId", "Edm.Guid"), new("LocationId", "Edm.Guid"), new("Status", "Edm.String"), new("Criticality", "Edm.String")],
            () => db.Assets.AsNoTracking(), a => a.Id,
            a => DatasetRow.Of(("Id", a.Id), ("Code", a.Code), ("Name", a.Name), ("AssetClassId", a.AssetClassId), ("LocationId", a.LocationId), ("Status", a.Status.ToString()), ("Criticality", a.Criticality.ToString())));

        yield return new Dataset<Domain.Location>("Locations", Permissions.LocationsRead,
            [new("Id", "Edm.Guid"), new("ParentId", "Edm.Guid"), new("Type", "Edm.String"), new("Code", "Edm.String"), new("Name", "Edm.String"), new("AreaSquareMetres", "Edm.Decimal"), new("IsArchived", "Edm.Boolean")],
            () => db.Locations.AsNoTracking(), l => l.Id,
            l => DatasetRow.Of(("Id", l.Id), ("ParentId", l.ParentId), ("Type", l.Type.ToString()), ("Code", l.Code), ("Name", l.Name), ("AreaSquareMetres", l.AreaSquareMetres), ("IsArchived", l.IsArchived)));
    }
}
