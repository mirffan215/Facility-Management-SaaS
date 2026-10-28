using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fms.Visitors.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantFilterIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Watchlist_TenantId",
                schema: "visitors",
                table: "Watchlist",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Visits_TenantId",
                schema: "visitors",
                table: "Visits",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Watchlist_TenantId",
                schema: "visitors",
                table: "Watchlist");

            migrationBuilder.DropIndex(
                name: "IX_Visits_TenantId",
                schema: "visitors",
                table: "Visits");
        }
    }
}
