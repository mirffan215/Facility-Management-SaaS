using System;
using Fms.BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fms.Integration.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialIntegration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "integration");

            migrationBuilder.CreateTable(
                name: "ErpExportItems",
                schema: "integration",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Entity = table.Column<int>(type: "int", nullable: false),
                    EntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LockedUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ErpReference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SucceededAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Reconcile = table.Column<int>(type: "int", nullable: false),
                    ReconciledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReconcileDetail = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ErpExportItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ErpSettings",
                schema: "integration",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    Adapter = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    CompanyId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    EnabledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ErpSettings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ErpExportItems_Status_NextAttemptAt",
                schema: "integration",
                table: "ErpExportItems",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ErpExportItems_TenantId",
                schema: "integration",
                table: "ErpExportItems",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ErpExportItems_TenantId_Entity_EntityId_Revision",
                schema: "integration",
                table: "ErpExportItems",
                columns: new[] { "TenantId", "Entity", "EntityId", "Revision" });

            migrationBuilder.CreateIndex(
                name: "IX_ErpExportItems_TenantId_IdempotencyKey",
                schema: "integration",
                table: "ErpExportItems",
                columns: new[] { "TenantId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ErpSettings_TenantId",
                schema: "integration",
                table: "ErpSettings",
                column: "TenantId",
                unique: true);
        
            migrationBuilder.ProtectTenantTable("ErpSettings", "integration");
            migrationBuilder.ProtectTenantTable("ErpExportItems", "integration");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UnprotectTenantTable("ErpExportItems", "integration");
            migrationBuilder.UnprotectTenantTable("ErpSettings", "integration");

            migrationBuilder.DropTable(
                name: "ErpExportItems",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "ErpSettings",
                schema: "integration");
        }
    }
}
