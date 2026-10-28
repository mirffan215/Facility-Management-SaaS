using Fms.BuildingBlocks.Reporting;
using Fms.BuildingBlocks.Security;
using Fms.Projects.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Projects.Services;

public static class ProjectsDatasets
{
    /// <summary>Projects and tasks by code and number: names, descriptions and people are left out.</summary>
    public static IEnumerable<IDatasetProvider> All(ProjectsDbContext db)
    {
        yield return new Dataset<Domain.Project>("Projects", Permissions.ProjectsRead,
            [new("Id", "Edm.Guid"), new("Code", "Edm.String"), new("Status", "Edm.String"), new("LocationId", "Edm.Guid"), new("StartOn", "Edm.String"), new("EndOn", "Edm.String"), new("ClosedOn", "Edm.String"),
             new("CurrencyCode", "Edm.String"), new("Budget", "Edm.Decimal"), new("CreatedAt", "Edm.DateTimeOffset")],
            () => db.Projects.AsNoTracking(), p => p.Id,
            p => DatasetRow.Of(("Id", p.Id), ("Code", p.Code), ("Status", p.Status.ToString()), ("LocationId", p.LocationId), ("StartOn", p.StartOn.ToString("yyyy-MM-dd")), ("EndOn", p.EndOn.ToString("yyyy-MM-dd")),
                ("ClosedOn", p.ClosedOn?.ToString("yyyy-MM-dd")), ("CurrencyCode", p.CurrencyCode), ("Budget", db.BudgetLines.Where(b => b.ProjectId == p.Id).Sum(b => (decimal?)b.Amount) ?? 0), ("CreatedAt", p.CreatedAt)),
            "CreatedAt", p => p.CreatedAt);
        yield return new Dataset<Domain.ProjectTask>("ProjectTasks", Permissions.ProjectsRead,
            [new("Id", "Edm.Guid"), new("ProjectId", "Edm.Guid"), new("Status", "Edm.String"), new("DueOn", "Edm.String"), new("DoneOn", "Edm.String")],
            () => db.Tasks.AsNoTracking(), t => t.Id,
            t => DatasetRow.Of(("Id", t.Id), ("ProjectId", t.ProjectId), ("Status", t.Status.ToString()), ("DueOn", t.DueOn?.ToString("yyyy-MM-dd")), ("DoneOn", t.DoneOn?.ToString("yyyy-MM-dd"))));
    }
}
