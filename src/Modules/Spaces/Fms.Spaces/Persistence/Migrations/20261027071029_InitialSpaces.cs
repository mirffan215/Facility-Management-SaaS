using System;
using Fms.BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fms.Spaces.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSpaces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "spaces");

            migrationBuilder.CreateTable(
                name: "Allocations",
                schema: "spaces",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SpaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TeamName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Headcount = table.Column<int>(type: "int", nullable: false),
                    StartOn = table.Column<DateOnly>(type: "date", nullable: false),
                    EndOn = table.Column<DateOnly>(type: "date", nullable: true),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CancelledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    EndedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EndReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Allocations", x => x.Id);
                    table.CheckConstraint("CK_Allocations_Dates", "[EndOn] IS NULL OR [EndOn] >= [StartOn]");
                    table.CheckConstraint("CK_Allocations_Headcount", "[Headcount] BETWEEN 1 AND 100000");
                    table.CheckConstraint("CK_Allocations_Occupant", "([Kind] = 1 AND [UserId] IS NOT NULL) OR ([Kind] = 2 AND [TeamName] IS NOT NULL)");
                });

            migrationBuilder.CreateTable(
                name: "Pins",
                schema: "spaces",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FloorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    TargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    X = table.Column<double>(type: "float", nullable: false),
                    Y = table.Column<double>(type: "float", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Pins", x => x.Id);
                    table.CheckConstraint("CK_Pins_Position", "[X] BETWEEN 0 AND 1 AND [Y] BETWEEN 0 AND 1");
                });

            migrationBuilder.CreateTable(
                name: "Plans",
                schema: "spaces",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FloorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    StorageKey = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    UploadedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UploadedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SupersededAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Plans", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Profiles",
                schema: "spaces",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Capacity = table.Column<int>(type: "int", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Profiles", x => x.Id);
                    table.CheckConstraint("CK_Profiles_Capacity", "[Capacity] BETWEEN 0 AND 100000");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Allocations_TenantId_SpaceId_StartOn",
                schema: "spaces",
                table: "Allocations",
                columns: new[] { "TenantId", "SpaceId", "StartOn" });

            migrationBuilder.CreateIndex(
                name: "IX_Allocations_TenantId_UserId",
                schema: "spaces",
                table: "Allocations",
                columns: new[] { "TenantId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_Pins_TenantId_FloorId_Kind_TargetId",
                schema: "spaces",
                table: "Pins",
                columns: new[] { "TenantId", "FloorId", "Kind", "TargetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Plans_TenantId_FloorId_SupersededAt",
                schema: "spaces",
                table: "Plans",
                columns: new[] { "TenantId", "FloorId", "SupersededAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Profiles_TenantId_LocationId",
                schema: "spaces",
                table: "Profiles",
                columns: new[] { "TenantId", "LocationId" },
                unique: true);

            migrationBuilder.ProtectTenantTable("Profiles", "spaces");
            migrationBuilder.ProtectTenantTable("Plans", "spaces");
            migrationBuilder.ProtectTenantTable("Pins", "spaces");
            migrationBuilder.ProtectTenantTable("Allocations", "spaces");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Allocations",
                schema: "spaces");

            migrationBuilder.DropTable(
                name: "Pins",
                schema: "spaces");

            migrationBuilder.DropTable(
                name: "Plans",
                schema: "spaces");

            migrationBuilder.DropTable(
                name: "Profiles",
                schema: "spaces");
        }
    }
}
