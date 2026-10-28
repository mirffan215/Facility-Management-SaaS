using System;
using Fms.BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fms.Leases.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialLeases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "leases");

            migrationBuilder.CreateTable(
                name: "Leases",
                schema: "leases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Counterparty = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    LocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LocationName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AreaSquareMetres = table.Column<decimal>(type: "decimal(12,2)", precision: 12, scale: 2, nullable: true),
                    StartOn = table.Column<DateOnly>(type: "date", nullable: false),
                    EndOn = table.Column<DateOnly>(type: "date", nullable: false),
                    NoticeDays = table.Column<int>(type: "int", nullable: false),
                    BreakOn = table.Column<DateOnly>(type: "date", nullable: true),
                    BreakNoticeDays = table.Column<int>(type: "int", nullable: false),
                    RenewalMonths = table.Column<int>(type: "int", nullable: true),
                    CurrencyCode = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    BaseRent = table.Column<decimal>(type: "decimal(19,2)", precision: 19, scale: 2, nullable: false),
                    Frequency = table.Column<int>(type: "int", nullable: false),
                    EscalationPercent = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    EscalationEveryMonths = table.Column<int>(type: "int", nullable: false),
                    RentFreeMonths = table.Column<int>(type: "int", nullable: false),
                    Deposit = table.Column<decimal>(type: "decimal(19,2)", precision: 19, scale: 2, nullable: true),
                    AlertLeadDays = table.Column<int>(type: "int", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ActivatedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    TerminatedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    TerminationReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    LastAlertKey = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    LastAlertOn = table.Column<DateOnly>(type: "date", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Leases", x => x.Id);
                    table.CheckConstraint("CK_Leases_Dates", "[EndOn] >= [StartOn]");
                    table.CheckConstraint("CK_Leases_Rent", "[BaseRent] >= 0 AND [EscalationPercent] >= 0 AND [EscalationPercent] <= 100");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Leases_TenantId",
                schema: "leases",
                table: "Leases",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Leases_TenantId_Reference",
                schema: "leases",
                table: "Leases",
                columns: new[] { "TenantId", "Reference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Leases_TenantId_Status_EndOn",
                schema: "leases",
                table: "Leases",
                columns: new[] { "TenantId", "Status", "EndOn" });
            migrationBuilder.ProtectTenantTable("Leases", "leases");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Leases",
                schema: "leases");
        }
    }
}
