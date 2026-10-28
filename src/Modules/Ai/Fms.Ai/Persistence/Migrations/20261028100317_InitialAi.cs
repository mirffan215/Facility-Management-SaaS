using System;
using Fms.BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fms.Ai.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialAi : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ai");

            migrationBuilder.CreateTable(
                name: "Settings",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    TriageEnabled = table.Column<bool>(type: "bit", nullable: false),
                    SummaryEnabled = table.Column<bool>(type: "bit", nullable: false),
                    DailyLimit = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Id);
                    table.CheckConstraint("CK_Settings_DailyLimit", "[DailyLimit] >= 0 AND [DailyLimit] <= 5000");
                });

            migrationBuilder.CreateTable(
                name: "Usage",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Feature = table.Column<int>(type: "int", nullable: false),
                    Outcome = table.Column<int>(type: "int", nullable: false),
                    InputTokens = table.Column<int>(type: "int", nullable: false),
                    OutputTokens = table.Column<int>(type: "int", nullable: false),
                    SubjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Usage", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Settings_TenantId",
                schema: "ai",
                table: "Settings",
                column: "TenantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Usage_TenantId",
                schema: "ai",
                table: "Usage",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Usage_TenantId_At",
                schema: "ai",
                table: "Usage",
                columns: new[] { "TenantId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_Usage_TenantId_UserId_At",
                schema: "ai",
                table: "Usage",
                columns: new[] { "TenantId", "UserId", "At" });
            migrationBuilder.ProtectTenantTable("Settings", "ai");
            migrationBuilder.ProtectTenantTable("Usage", "ai");

            migrationBuilder.Sql(@"CREATE TRIGGER ai.TR_Usage_AppendOnly ON ai.Usage INSTEAD OF UPDATE, DELETE AS
BEGIN
    THROW 51061, 'Usage is append-only.', 1;
END");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Settings",
                schema: "ai");

            migrationBuilder.DropTable(
                name: "Usage",
                schema: "ai");
        }
    }
}
