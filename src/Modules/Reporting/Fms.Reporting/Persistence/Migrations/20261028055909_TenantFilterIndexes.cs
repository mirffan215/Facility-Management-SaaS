using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fms.Reporting.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantFilterIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_FeedTokens_TenantId",
                schema: "reporting",
                table: "FeedTokens",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FeedTokens_TenantId",
                schema: "reporting",
                table: "FeedTokens");
        }
    }
}
