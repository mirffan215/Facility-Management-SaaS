using System;
using Fms.BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fms.Work.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PreventiveMaintenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PmTemplates",
                schema: "work",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Instructions = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    EstimatedMinutes = table.Column<int>(type: "int", nullable: true),
                    CategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ChecklistTemplateId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PmTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PmTemplates_Categories_CategoryId",
                        column: x => x.CategoryId,
                        principalSchema: "work",
                        principalTable: "Categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PmTemplates_ChecklistTemplates_ChecklistTemplateId",
                        column: x => x.ChecklistTemplateId,
                        principalSchema: "work",
                        principalTable: "ChecklistTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PmSchedules",
                schema: "work",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TemplateId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetKind = table.Column<int>(type: "int", nullable: false),
                    AssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AssetClassId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TimeBasis = table.Column<int>(type: "int", nullable: false),
                    IntervalUnit = table.Column<int>(type: "int", nullable: false),
                    Interval = table.Column<int>(type: "int", nullable: false),
                    StartOn = table.Column<DateOnly>(type: "date", nullable: false),
                    LeadDays = table.Column<int>(type: "int", nullable: false),
                    MeterName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    MeterInterval = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    AssignToUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PmSchedules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PmSchedules_PmTemplates_TemplateId",
                        column: x => x.TemplateId,
                        principalSchema: "work",
                        principalTable: "PmTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PmPlans",
                schema: "work",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ScheduleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NextDueOn = table.Column<DateOnly>(type: "date", nullable: true),
                    LastCompletedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    OpenWorkOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MeterId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LastServiceMeterValue = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PmPlans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PmPlans_PmSchedules_ScheduleId",
                        column: x => x.ScheduleId,
                        principalSchema: "work",
                        principalTable: "PmSchedules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PmOccurrences",
                schema: "work",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlanId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ScheduleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DueOn = table.Column<DateOnly>(type: "date", nullable: false),
                    Trigger = table.Column<int>(type: "int", nullable: false),
                    GeneratedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Outcome = table.Column<int>(type: "int", nullable: false),
                    FinishedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    OnTime = table.Column<bool>(type: "bit", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PmOccurrences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PmOccurrences_PmPlans_PlanId",
                        column: x => x.PlanId,
                        principalSchema: "work",
                        principalTable: "PmPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PmOccurrences_PlanId",
                schema: "work",
                table: "PmOccurrences",
                column: "PlanId");

            migrationBuilder.CreateIndex(
                name: "IX_PmOccurrences_TenantId",
                schema: "work",
                table: "PmOccurrences",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PmOccurrences_TenantId_DueOn",
                schema: "work",
                table: "PmOccurrences",
                columns: new[] { "TenantId", "DueOn" });

            migrationBuilder.CreateIndex(
                name: "IX_PmOccurrences_TenantId_PlanId_DueOn_Trigger",
                schema: "work",
                table: "PmOccurrences",
                columns: new[] { "TenantId", "PlanId", "DueOn", "Trigger" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PmOccurrences_TenantId_WorkOrderId",
                schema: "work",
                table: "PmOccurrences",
                columns: new[] { "TenantId", "WorkOrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_PmPlans_ScheduleId",
                schema: "work",
                table: "PmPlans",
                column: "ScheduleId");

            migrationBuilder.CreateIndex(
                name: "IX_PmPlans_TenantId",
                schema: "work",
                table: "PmPlans",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PmPlans_TenantId_IsActive_NextDueOn",
                schema: "work",
                table: "PmPlans",
                columns: new[] { "TenantId", "IsActive", "NextDueOn" });

            migrationBuilder.CreateIndex(
                name: "IX_PmPlans_TenantId_OpenWorkOrderId",
                schema: "work",
                table: "PmPlans",
                columns: new[] { "TenantId", "OpenWorkOrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_PmPlans_TenantId_ScheduleId_AssetId",
                schema: "work",
                table: "PmPlans",
                columns: new[] { "TenantId", "ScheduleId", "AssetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PmSchedules_TemplateId",
                schema: "work",
                table: "PmSchedules",
                column: "TemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_PmSchedules_TenantId",
                schema: "work",
                table: "PmSchedules",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PmSchedules_TenantId_AssetClassId",
                schema: "work",
                table: "PmSchedules",
                columns: new[] { "TenantId", "AssetClassId" });

            migrationBuilder.CreateIndex(
                name: "IX_PmSchedules_TenantId_AssetId",
                schema: "work",
                table: "PmSchedules",
                columns: new[] { "TenantId", "AssetId" });

            migrationBuilder.CreateIndex(
                name: "IX_PmSchedules_TenantId_TemplateId",
                schema: "work",
                table: "PmSchedules",
                columns: new[] { "TenantId", "TemplateId" });

            migrationBuilder.CreateIndex(
                name: "IX_PmTemplates_CategoryId",
                schema: "work",
                table: "PmTemplates",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_PmTemplates_ChecklistTemplateId",
                schema: "work",
                table: "PmTemplates",
                column: "ChecklistTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_PmTemplates_TenantId",
                schema: "work",
                table: "PmTemplates",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PmTemplates_TenantId_Name",
                schema: "work",
                table: "PmTemplates",
                columns: new[] { "TenantId", "Name" },
                unique: true,
                filter: "[IsArchived] = 0");

            migrationBuilder.ProtectTenantTable("PmTemplates", "work");
            migrationBuilder.ProtectTenantTable("PmSchedules", "work");
            migrationBuilder.ProtectTenantTable("PmPlans", "work");
            migrationBuilder.ProtectTenantTable("PmOccurrences", "work");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UnprotectTenantTable("PmOccurrences", "work");
            migrationBuilder.UnprotectTenantTable("PmPlans", "work");
            migrationBuilder.UnprotectTenantTable("PmSchedules", "work");
            migrationBuilder.UnprotectTenantTable("PmTemplates", "work");

            migrationBuilder.DropTable(
                name: "PmOccurrences",
                schema: "work");

            migrationBuilder.DropTable(
                name: "PmPlans",
                schema: "work");

            migrationBuilder.DropTable(
                name: "PmSchedules",
                schema: "work");

            migrationBuilder.DropTable(
                name: "PmTemplates",
                schema: "work");
        }
    }
}
