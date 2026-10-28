using System;
using Fms.BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fms.Energy.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialEnergy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "energy");

            migrationBuilder.CreateTable(
                name: "AlarmEvents",
                schema: "energy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RuleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MeterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Value = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlarmEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AlarmRules",
                schema: "energy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MeterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Threshold = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    IsRaised = table.Column<bool>(type: "bit", nullable: false),
                    RaisedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlarmRules", x => x.Id);
                    table.CheckConstraint("CK_AlarmRules_Threshold", "[Threshold] > 0");
                });

            migrationBuilder.CreateTable(
                name: "Devices",
                schema: "energy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Prefix = table.Column<string>(type: "nvarchar(12)", maxLength: 12, nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    MeterIdsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RevokedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Devices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Meters",
                schema: "energy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Unit = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    LocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LocationName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IsCumulative = table.Column<bool>(type: "bit", nullable: false),
                    RolloverAt = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Meters", x => x.Id);
                    table.CheckConstraint("CK_Meters_Rollover", "[RolloverAt] IS NULL OR [RolloverAt] > 0");
                });

            migrationBuilder.CreateTable(
                name: "Readings",
                schema: "energy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MeterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Value = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RecordedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RecordedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Note = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Readings", x => x.Id);
                    table.CheckConstraint("CK_Readings_Value", "[Value] >= 0");
                });

            migrationBuilder.CreateTable(
                name: "ReadingVoids",
                schema: "energy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReadingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    By = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReadingVoids", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tariffs",
                schema: "energy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    PricePerUnit = table.Column<decimal>(type: "decimal(19,6)", precision: 19, scale: 6, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tariffs", x => x.Id);
                    table.CheckConstraint("CK_Tariffs_Price", "[PricePerUnit] >= 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_AlarmEvents_TenantId",
                schema: "energy",
                table: "AlarmEvents",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_AlarmEvents_TenantId_MeterId_At",
                schema: "energy",
                table: "AlarmEvents",
                columns: new[] { "TenantId", "MeterId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_AlarmRules_TenantId",
                schema: "energy",
                table: "AlarmRules",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_AlarmRules_TenantId_MeterId_Kind",
                schema: "energy",
                table: "AlarmRules",
                columns: new[] { "TenantId", "MeterId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Devices_TenantId",
                schema: "energy",
                table: "Devices",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Devices_TokenHash",
                schema: "energy",
                table: "Devices",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Meters_TenantId",
                schema: "energy",
                table: "Meters",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Meters_TenantId_Code",
                schema: "energy",
                table: "Meters",
                columns: new[] { "TenantId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Meters_TenantId_LocationId",
                schema: "energy",
                table: "Meters",
                columns: new[] { "TenantId", "LocationId" });

            migrationBuilder.CreateIndex(
                name: "IX_Readings_TenantId",
                schema: "energy",
                table: "Readings",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Readings_TenantId_MeterId_At",
                schema: "energy",
                table: "Readings",
                columns: new[] { "TenantId", "MeterId", "At" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReadingVoids_TenantId",
                schema: "energy",
                table: "ReadingVoids",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ReadingVoids_TenantId_ReadingId",
                schema: "energy",
                table: "ReadingVoids",
                columns: new[] { "TenantId", "ReadingId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tariffs_TenantId",
                schema: "energy",
                table: "Tariffs",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Tariffs_TenantId_Kind_EffectiveFrom",
                schema: "energy",
                table: "Tariffs",
                columns: new[] { "TenantId", "Kind", "EffectiveFrom" },
                unique: true);
            migrationBuilder.ProtectTenantTable("Meters", "energy");
            migrationBuilder.ProtectTenantTable("Readings", "energy");
            migrationBuilder.ProtectTenantTable("ReadingVoids", "energy");
            migrationBuilder.ProtectTenantTable("Tariffs", "energy");
            migrationBuilder.ProtectTenantTable("Devices", "energy");
            migrationBuilder.ProtectTenantTable("AlarmRules", "energy");
            migrationBuilder.ProtectTenantTable("AlarmEvents", "energy");
            migrationBuilder.Sql(@"CREATE TRIGGER energy.TR_Readings_AppendOnly ON energy.Readings INSTEAD OF UPDATE, DELETE AS
BEGIN
    THROW 51051, 'Readings is append-only.', 1;
END");
            migrationBuilder.Sql(@"CREATE TRIGGER energy.TR_ReadingVoids_AppendOnly ON energy.ReadingVoids INSTEAD OF UPDATE, DELETE AS
BEGIN
    THROW 51052, 'ReadingVoids is append-only.', 1;
END");
            migrationBuilder.Sql(@"CREATE TRIGGER energy.TR_AlarmEvents_AppendOnly ON energy.AlarmEvents INSTEAD OF UPDATE, DELETE AS
BEGIN
    THROW 51053, 'AlarmEvents is append-only.', 1;
END");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlarmEvents",
                schema: "energy");

            migrationBuilder.DropTable(
                name: "AlarmRules",
                schema: "energy");

            migrationBuilder.DropTable(
                name: "Devices",
                schema: "energy");

            migrationBuilder.DropTable(
                name: "Meters",
                schema: "energy");

            migrationBuilder.DropTable(
                name: "Readings",
                schema: "energy");

            migrationBuilder.DropTable(
                name: "ReadingVoids",
                schema: "energy");

            migrationBuilder.DropTable(
                name: "Tariffs",
                schema: "energy");
        }
    }
}
