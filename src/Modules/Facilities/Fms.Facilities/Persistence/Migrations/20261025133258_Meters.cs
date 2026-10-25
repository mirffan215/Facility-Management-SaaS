using System;
using Fms.BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fms.Facilities.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Meters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Meters",
                schema: "facilities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Unit = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    MinValue = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    MaxValue = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Meters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Meters_Assets_AssetId",
                        column: x => x.AssetId,
                        principalSchema: "facilities",
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MeterReadings",
                schema: "facilities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MeterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Value = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    ReadAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RecordedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Source = table.Column<int>(type: "int", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsReset = table.Column<bool>(type: "bit", nullable: false),
                    ClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeterReadings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MeterReadings_Meters_MeterId",
                        column: x => x.MeterId,
                        principalSchema: "facilities",
                        principalTable: "Meters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MeterReadingVoids",
                schema: "facilities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReadingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VoidedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    VoidedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeterReadingVoids", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MeterReadingVoids_MeterReadings_ReadingId",
                        column: x => x.ReadingId,
                        principalSchema: "facilities",
                        principalTable: "MeterReadings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MeterReadings_MeterId",
                schema: "facilities",
                table: "MeterReadings",
                column: "MeterId");

            migrationBuilder.CreateIndex(
                name: "IX_MeterReadings_TenantId",
                schema: "facilities",
                table: "MeterReadings",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_MeterReadings_TenantId_MeterId_ClientId",
                schema: "facilities",
                table: "MeterReadings",
                columns: new[] { "TenantId", "MeterId", "ClientId" },
                unique: true,
                filter: "[ClientId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MeterReadings_TenantId_MeterId_ReadAt",
                schema: "facilities",
                table: "MeterReadings",
                columns: new[] { "TenantId", "MeterId", "ReadAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MeterReadingVoids_ReadingId",
                schema: "facilities",
                table: "MeterReadingVoids",
                column: "ReadingId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MeterReadingVoids_TenantId",
                schema: "facilities",
                table: "MeterReadingVoids",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Meters_AssetId",
                schema: "facilities",
                table: "Meters",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_Meters_TenantId",
                schema: "facilities",
                table: "Meters",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Meters_TenantId_AssetId_Name",
                schema: "facilities",
                table: "Meters",
                columns: new[] { "TenantId", "AssetId", "Name" },
                unique: true,
                filter: "[IsArchived] = 0");

            migrationBuilder.ProtectTenantTable("Meters", "facilities");
            migrationBuilder.ProtectTenantTable("MeterReadings", "facilities");
            migrationBuilder.ProtectTenantTable("MeterReadingVoids", "facilities");

            // Readings and voids are evidence: the database itself refuses to change or remove them.
            migrationBuilder.Sql(@"CREATE TRIGGER facilities.TR_MeterReadings_AppendOnly ON facilities.MeterReadings INSTEAD OF UPDATE, DELETE AS
BEGIN
    THROW 51001, 'MeterReadings is append-only.', 1;
END");
            migrationBuilder.Sql(@"CREATE TRIGGER facilities.TR_MeterReadingVoids_AppendOnly ON facilities.MeterReadingVoids INSTEAD OF UPDATE, DELETE AS
BEGIN
    THROW 51002, 'MeterReadingVoids is append-only.', 1;
END");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS facilities.TR_MeterReadingVoids_AppendOnly;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS facilities.TR_MeterReadings_AppendOnly;");
            migrationBuilder.UnprotectTenantTable("MeterReadingVoids", "facilities");
            migrationBuilder.UnprotectTenantTable("MeterReadings", "facilities");
            migrationBuilder.UnprotectTenantTable("Meters", "facilities");

            migrationBuilder.DropTable(
                name: "MeterReadingVoids",
                schema: "facilities");

            migrationBuilder.DropTable(
                name: "MeterReadings",
                schema: "facilities");

            migrationBuilder.DropTable(
                name: "Meters",
                schema: "facilities");
        }
    }
}
