using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fms.Work.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkOrderSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SourceId",
                schema: "work",
                table: "WorkOrders",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceKind",
                schema: "work",
                table: "WorkOrders",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_TenantId_SourceKind_SourceId",
                schema: "work",
                table: "WorkOrders",
                columns: new[] { "TenantId", "SourceKind", "SourceId" },
                unique: true,
                filter: "[SourceId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkOrders_TenantId_SourceKind_SourceId",
                schema: "work",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "SourceId",
                schema: "work",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "SourceKind",
                schema: "work",
                table: "WorkOrders");
        }
    }
}
