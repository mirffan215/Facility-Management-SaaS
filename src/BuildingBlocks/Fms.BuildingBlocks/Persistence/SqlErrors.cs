using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Fms.BuildingBlocks.Persistence;

public static class SqlErrors
{
    /// <summary>True when the save failed because a unique index or primary key would be violated.</summary>
    public static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is SqlException { Number: 2601 or 2627 };
}
