using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fms.Work.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PmMeterVisits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PmOccurrences_TenantId_PlanId_DueOn_Trigger",
                schema: "work",
                table: "PmOccurrences");

            migrationBuilder.CreateIndex(
                name: "IX_PmOccurrences_TenantId_PlanId_DueOn_Trigger",
                schema: "work",
                table: "PmOccurrences",
                columns: new[] { "TenantId", "PlanId", "DueOn", "Trigger" },
                unique: true,
                filter: "[Trigger] = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PmOccurrences_TenantId_PlanId_DueOn_Trigger",
                schema: "work",
                table: "PmOccurrences");

            migrationBuilder.CreateIndex(
                name: "IX_PmOccurrences_TenantId_PlanId_DueOn_Trigger",
                schema: "work",
                table: "PmOccurrences",
                columns: new[] { "TenantId", "PlanId", "DueOn", "Trigger" },
                unique: true);
        }
    }
}
