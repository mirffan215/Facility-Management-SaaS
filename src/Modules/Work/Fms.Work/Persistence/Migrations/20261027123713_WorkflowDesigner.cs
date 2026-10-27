using System;
using Fms.BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fms.Work.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkflowDesigner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RequirePhotoOnCompletion",
                schema: "work",
                table: "Settings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RequireTimeOnCompletion",
                schema: "work",
                table: "Settings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "VerificationPriorities",
                schema: "work",
                table: "Settings",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "WaivedApprovalPriorities",
                schema: "work",
                table: "Settings",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "WorkflowVersions",
                schema: "work",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    RulesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(600)", maxLength: 600, nullable: false),
                    ChangedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ChangedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowVersions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowVersions_TenantId",
                schema: "work",
                table: "WorkflowVersions",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowVersions_TenantId_Version",
                schema: "work",
                table: "WorkflowVersions",
                columns: new[] { "TenantId", "Version" },
                unique: true);

            migrationBuilder.ProtectTenantTable("WorkflowVersions", "work");
            migrationBuilder.Sql(@"CREATE TRIGGER work.TR_WorkflowVersions_AppendOnly ON work.WorkflowVersions INSTEAD OF UPDATE, DELETE AS
BEGIN
    THROW 51031, 'WorkflowVersions is append-only.', 1;
END");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkflowVersions",
                schema: "work");

            migrationBuilder.DropColumn(
                name: "RequirePhotoOnCompletion",
                schema: "work",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "RequireTimeOnCompletion",
                schema: "work",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "VerificationPriorities",
                schema: "work",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "WaivedApprovalPriorities",
                schema: "work",
                table: "Settings");
        }
    }
}
