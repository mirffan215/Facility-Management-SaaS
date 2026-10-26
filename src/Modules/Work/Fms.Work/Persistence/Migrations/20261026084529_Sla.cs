using System;
using Fms.BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fms.Work.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Sla : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ResolutionBreachNotifiedAt",
                schema: "work",
                table: "WorkOrders",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ResolutionDueAt",
                schema: "work",
                table: "WorkOrders",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ResolutionRemainingMinutes",
                schema: "work",
                table: "WorkOrders",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ResolutionWarnedAt",
                schema: "work",
                table: "WorkOrders",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ResolutionWindowStartAt",
                schema: "work",
                table: "WorkOrders",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RespondedAt",
                schema: "work",
                table: "WorkOrders",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ResponseBreachNotifiedAt",
                schema: "work",
                table: "WorkOrders",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ResponseDueAt",
                schema: "work",
                table: "WorkOrders",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SlaCalendarId",
                schema: "work",
                table: "WorkOrders",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SlaPausedAt",
                schema: "work",
                table: "WorkOrders",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SlaPolicyId",
                schema: "work",
                table: "WorkOrders",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SlaStartedAt",
                schema: "work",
                table: "WorkOrders",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Calendars",
                schema: "work",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TimeZoneId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    HoursJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Calendars", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SlaPolicies",
                schema: "work",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlaPolicies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SlaPolicies_Categories_CategoryId",
                        column: x => x.CategoryId,
                        principalSchema: "work",
                        principalTable: "Categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CalendarHolidays",
                schema: "work",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CalendarId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarHolidays", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CalendarHolidays_Calendars_CalendarId",
                        column: x => x.CalendarId,
                        principalSchema: "work",
                        principalTable: "Calendars",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CalendarSites",
                schema: "work",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CalendarId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarSites", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CalendarSites_Calendars_CalendarId",
                        column: x => x.CalendarId,
                        principalSchema: "work",
                        principalTable: "Calendars",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SlaTargets",
                schema: "work",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PolicyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    ResponseMinutes = table.Column<int>(type: "int", nullable: false),
                    ResolutionMinutes = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlaTargets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SlaTargets_SlaPolicies_PolicyId",
                        column: x => x.PolicyId,
                        principalSchema: "work",
                        principalTable: "SlaPolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_TenantId_ResolutionDueAt",
                schema: "work",
                table: "WorkOrders",
                columns: new[] { "TenantId", "ResolutionDueAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarHolidays_CalendarId",
                schema: "work",
                table: "CalendarHolidays",
                column: "CalendarId");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarHolidays_TenantId",
                schema: "work",
                table: "CalendarHolidays",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarHolidays_TenantId_CalendarId_Date",
                schema: "work",
                table: "CalendarHolidays",
                columns: new[] { "TenantId", "CalendarId", "Date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Calendars_OneDefault",
                schema: "work",
                table: "Calendars",
                column: "TenantId",
                unique: true,
                filter: "[IsDefault] = 1 AND [IsArchived] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_Calendars_TenantId_Name",
                schema: "work",
                table: "Calendars",
                columns: new[] { "TenantId", "Name" },
                unique: true,
                filter: "[IsArchived] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarSites_CalendarId",
                schema: "work",
                table: "CalendarSites",
                column: "CalendarId");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarSites_TenantId",
                schema: "work",
                table: "CalendarSites",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarSites_TenantId_SiteId",
                schema: "work",
                table: "CalendarSites",
                columns: new[] { "TenantId", "SiteId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SlaPolicies_CategoryId",
                schema: "work",
                table: "SlaPolicies",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_SlaPolicies_OneDefault",
                schema: "work",
                table: "SlaPolicies",
                column: "TenantId",
                unique: true,
                filter: "[IsDefault] = 1 AND [IsArchived] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_SlaPolicies_OnePerCategory",
                schema: "work",
                table: "SlaPolicies",
                columns: new[] { "TenantId", "CategoryId" },
                unique: true,
                filter: "[CategoryId] IS NOT NULL AND [IsArchived] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_SlaTargets_PolicyId",
                schema: "work",
                table: "SlaTargets",
                column: "PolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_SlaTargets_TenantId",
                schema: "work",
                table: "SlaTargets",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_SlaTargets_TenantId_PolicyId_Priority",
                schema: "work",
                table: "SlaTargets",
                columns: new[] { "TenantId", "PolicyId", "Priority" },
                unique: true);

            migrationBuilder.ProtectTenantTable("Calendars", "work");
            migrationBuilder.ProtectTenantTable("CalendarHolidays", "work");
            migrationBuilder.ProtectTenantTable("CalendarSites", "work");
            migrationBuilder.ProtectTenantTable("SlaPolicies", "work");
            migrationBuilder.ProtectTenantTable("SlaTargets", "work");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UnprotectTenantTable("SlaTargets", "work");
            migrationBuilder.UnprotectTenantTable("SlaPolicies", "work");
            migrationBuilder.UnprotectTenantTable("CalendarSites", "work");
            migrationBuilder.UnprotectTenantTable("CalendarHolidays", "work");
            migrationBuilder.UnprotectTenantTable("Calendars", "work");

            migrationBuilder.DropTable(
                name: "CalendarHolidays",
                schema: "work");

            migrationBuilder.DropTable(
                name: "CalendarSites",
                schema: "work");

            migrationBuilder.DropTable(
                name: "SlaTargets",
                schema: "work");

            migrationBuilder.DropTable(
                name: "Calendars",
                schema: "work");

            migrationBuilder.DropTable(
                name: "SlaPolicies",
                schema: "work");

            migrationBuilder.DropIndex(
                name: "IX_WorkOrders_TenantId_ResolutionDueAt",
                schema: "work",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "ResolutionBreachNotifiedAt",
                schema: "work",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "ResolutionDueAt",
                schema: "work",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "ResolutionRemainingMinutes",
                schema: "work",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "ResolutionWarnedAt",
                schema: "work",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "ResolutionWindowStartAt",
                schema: "work",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "RespondedAt",
                schema: "work",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "ResponseBreachNotifiedAt",
                schema: "work",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "ResponseDueAt",
                schema: "work",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "SlaCalendarId",
                schema: "work",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "SlaPausedAt",
                schema: "work",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "SlaPolicyId",
                schema: "work",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "SlaStartedAt",
                schema: "work",
                table: "WorkOrders");
        }
    }
}
