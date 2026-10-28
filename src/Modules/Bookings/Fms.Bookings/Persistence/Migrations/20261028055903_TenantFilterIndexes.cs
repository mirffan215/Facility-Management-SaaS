using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fms.Bookings.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantFilterIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Resources_TenantId",
                schema: "bookings",
                table: "Resources",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarLinks_TenantId",
                schema: "bookings",
                table: "CalendarLinks",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarFeedTokens_TenantId",
                schema: "bookings",
                table: "CalendarFeedTokens",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_TenantId",
                schema: "bookings",
                table: "Bookings",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Resources_TenantId",
                schema: "bookings",
                table: "Resources");

            migrationBuilder.DropIndex(
                name: "IX_CalendarLinks_TenantId",
                schema: "bookings",
                table: "CalendarLinks");

            migrationBuilder.DropIndex(
                name: "IX_CalendarFeedTokens_TenantId",
                schema: "bookings",
                table: "CalendarFeedTokens");

            migrationBuilder.DropIndex(
                name: "IX_Bookings_TenantId",
                schema: "bookings",
                table: "Bookings");
        }
    }
}
