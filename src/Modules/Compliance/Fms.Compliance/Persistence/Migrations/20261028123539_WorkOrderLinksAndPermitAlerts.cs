using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fms.Compliance.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkOrderLinksAndPermitAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "LastOverdueAlertOn",
                schema: "compliance",
                table: "Permits",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CreateWorkOrderOnFail",
                schema: "compliance",
                table: "Obligations",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastOverdueAlertOn",
                schema: "compliance",
                table: "Permits");

            migrationBuilder.DropColumn(
                name: "CreateWorkOrderOnFail",
                schema: "compliance",
                table: "Obligations");
        }
    }
}
