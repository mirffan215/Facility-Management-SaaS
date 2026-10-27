using Fms.BuildingBlocks.Reporting;
using Fms.BuildingBlocks.Security;
using Fms.Bookings.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Bookings.Services;

public static class BookingsDatasets
{
    public static IEnumerable<IDatasetProvider> All(BookingsDbContext db)
    {
        yield return new Dataset<Domain.Booking>("Bookings", Permissions.BookingsApprove,
            [new("Id", "Edm.Guid"), new("ResourceId", "Edm.Guid"), new("Status", "Edm.String"), new("Attendees", "Edm.Int32"), new("StartsAt", "Edm.DateTimeOffset"), new("EndsAt", "Edm.DateTimeOffset"),
             new("DurationHours", "Edm.Decimal"), new("CheckedInAt", "Edm.DateTimeOffset"), new("CreatedAt", "Edm.DateTimeOffset")],
            () => db.Bookings.AsNoTracking(), b => b.Id,
            b => DatasetRow.Of(("Id", b.Id), ("ResourceId", b.ResourceId), ("Status", b.Status.ToString()), ("Attendees", b.Attendees), ("StartsAt", b.StartsAt), ("EndsAt", b.EndsAt),
                ("DurationHours", Math.Round((decimal)(b.EndsAt - b.StartsAt).TotalHours, 2)), ("CheckedInAt", b.CheckedInAt), ("CreatedAt", b.CreatedAt)),
            "StartsAt", b => b.StartsAt);

        yield return new Dataset<Domain.BookableResource>("BookableResources", Permissions.BookingsApprove,
            [new("Id", "Edm.Guid"), new("Code", "Edm.String"), new("Name", "Edm.String"), new("Kind", "Edm.String"), new("SpaceId", "Edm.Guid"), new("Capacity", "Edm.Int32"), new("RequiresApproval", "Edm.Boolean"), new("IsArchived", "Edm.Boolean")],
            () => db.Resources.AsNoTracking(), r => r.Id,
            r => DatasetRow.Of(("Id", r.Id), ("Code", r.Code), ("Name", r.Name), ("Kind", r.Kind.ToString()), ("SpaceId", r.SpaceId), ("Capacity", r.Capacity), ("RequiresApproval", r.RequiresApproval), ("IsArchived", r.IsArchived)));
    }
}
