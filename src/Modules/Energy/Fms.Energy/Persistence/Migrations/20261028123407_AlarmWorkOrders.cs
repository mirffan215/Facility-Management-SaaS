using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fms.Energy.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AlarmWorkOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CreateWorkOrder",
                schema: "energy",
                table: "AlarmRules",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "WorkOrderRequesterId",
                schema: "energy",
                table: "AlarmRules",
                type: "uniqueidentifier",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreateWorkOrder",
                schema: "energy",
                table: "AlarmRules");

            migrationBuilder.DropColumn(
                name: "WorkOrderRequesterId",
                schema: "energy",
                table: "AlarmRules");
        }
    }
}
