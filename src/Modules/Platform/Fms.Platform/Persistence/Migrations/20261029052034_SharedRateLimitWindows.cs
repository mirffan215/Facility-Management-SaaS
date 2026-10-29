using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fms.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SharedRateLimitWindows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A counter shared by every server, not owned by any tenant (it counts anonymous callers by address). Written by SharedRateLimiter with raw SQL, so EF does not model it.
            migrationBuilder.Sql(@"CREATE TABLE dbo.RateLimitWindows (
    [Key] nvarchar(100) NOT NULL,
    WindowStart datetime2 NOT NULL,
    Hits int NOT NULL,
    CONSTRAINT PK_RateLimitWindows PRIMARY KEY ([Key], WindowStart)
);
CREATE INDEX IX_RateLimitWindows_WindowStart ON dbo.RateLimitWindows (WindowStart);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE dbo.RateLimitWindows;");
        }
    }
}
