using System;
using Fms.BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fms.Vendors.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class VendorPortalUsers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VendorUsers",
                schema: "vendors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VendorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LinkedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    DeactivatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VendorUsers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VendorUsers_TenantId",
                schema: "vendors",
                table: "VendorUsers",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_VendorUsers_TenantId_UserId",
                schema: "vendors",
                table: "VendorUsers",
                columns: new[] { "TenantId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VendorUsers_TenantId_VendorId",
                schema: "vendors",
                table: "VendorUsers",
                columns: new[] { "TenantId", "VendorId" });
        
            migrationBuilder.ProtectTenantTable("VendorUsers", "vendors");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UnprotectTenantTable("VendorUsers", "vendors");

            migrationBuilder.DropTable(
                name: "VendorUsers",
                schema: "vendors");
        }
    }
}
