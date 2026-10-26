using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fms.Work.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkOrderVendor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AssignedVendorId",
                schema: "work",
                table: "WorkOrders",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "VendorContractId",
                schema: "work",
                table: "WorkOrders",
                type: "uniqueidentifier",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AssignedVendorId",
                schema: "work",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "VendorContractId",
                schema: "work",
                table: "WorkOrders");
        }
    }
}
