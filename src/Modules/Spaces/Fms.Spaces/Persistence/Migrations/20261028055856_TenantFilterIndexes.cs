using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fms.Spaces.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantFilterIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Profiles_TenantId",
                schema: "spaces",
                table: "Profiles",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Plans_TenantId",
                schema: "spaces",
                table: "Plans",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Pins_TenantId",
                schema: "spaces",
                table: "Pins",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Allocations_TenantId",
                schema: "spaces",
                table: "Allocations",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Profiles_TenantId",
                schema: "spaces",
                table: "Profiles");

            migrationBuilder.DropIndex(
                name: "IX_Plans_TenantId",
                schema: "spaces",
                table: "Plans");

            migrationBuilder.DropIndex(
                name: "IX_Pins_TenantId",
                schema: "spaces",
                table: "Pins");

            migrationBuilder.DropIndex(
                name: "IX_Allocations_TenantId",
                schema: "spaces",
                table: "Allocations");
        }
    }
}
