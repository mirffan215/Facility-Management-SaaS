using System;
using Fms.BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fms.Work.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Evidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CurrencyCode",
                schema: "work",
                table: "Settings",
                type: "nvarchar(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "SAR");

            migrationBuilder.AddColumn<decimal>(
                name: "LabourRatePerHour",
                schema: "work",
                table: "Settings",
                type: "decimal(12,2)",
                precision: 12,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<bool>(
                name: "RequireCompletionSignature",
                schema: "work",
                table: "Settings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ChecklistTemplates",
                schema: "work",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChecklistTemplates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkOrderAttachments",
                schema: "work",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    StorageKey = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    UploadedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UploadedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeletedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeleteReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkOrderAttachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkOrderAttachments_WorkOrders_WorkOrderId",
                        column: x => x.WorkOrderId,
                        principalSchema: "work",
                        principalTable: "WorkOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkOrderChecklistItems",
                schema: "work",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TemplateId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Position = table.Column<int>(type: "int", nullable: false),
                    Text = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Required = table.Column<bool>(type: "bit", nullable: false),
                    Result = table.Column<int>(type: "int", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ResultBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ResultAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkOrderChecklistItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkOrderChecklistItems_WorkOrders_WorkOrderId",
                        column: x => x.WorkOrderId,
                        principalSchema: "work",
                        principalTable: "WorkOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkOrderCostLines",
                schema: "work",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Quantity = table.Column<decimal>(type: "decimal(12,3)", precision: 12, scale: 3, nullable: false),
                    UnitCost = table.Column<decimal>(type: "decimal(14,4)", precision: 14, scale: 4, nullable: false),
                    RecordedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkOrderCostLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkOrderCostLines_WorkOrders_WorkOrderId",
                        column: x => x.WorkOrderId,
                        principalSchema: "work",
                        principalTable: "WorkOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkOrderSignatures",
                schema: "work",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Role = table.Column<int>(type: "int", nullable: false),
                    SignerName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    StorageKey = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Sha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SignedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RecordedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkOrderSignatures", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkOrderSignatures_WorkOrders_WorkOrderId",
                        column: x => x.WorkOrderId,
                        principalSchema: "work",
                        principalTable: "WorkOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkOrderTimeEntries",
                schema: "work",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    Minutes = table.Column<int>(type: "int", nullable: false),
                    HourlyRate = table.Column<decimal>(type: "decimal(12,2)", precision: 12, scale: 2, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RecordedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkOrderTimeEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkOrderTimeEntries_WorkOrders_WorkOrderId",
                        column: x => x.WorkOrderId,
                        principalSchema: "work",
                        principalTable: "WorkOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkOrderVoids",
                schema: "work",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntryType = table.Column<int>(type: "int", nullable: false),
                    EntryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    VoidedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VoidedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkOrderVoids", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkOrderVoids_WorkOrders_WorkOrderId",
                        column: x => x.WorkOrderId,
                        principalSchema: "work",
                        principalTable: "WorkOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ChecklistTemplateItems",
                schema: "work",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TemplateId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false),
                    Text = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Required = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChecklistTemplateItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChecklistTemplateItems_ChecklistTemplates_TemplateId",
                        column: x => x.TemplateId,
                        principalSchema: "work",
                        principalTable: "ChecklistTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChecklistTemplateItems_TemplateId",
                schema: "work",
                table: "ChecklistTemplateItems",
                column: "TemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_ChecklistTemplateItems_TenantId",
                schema: "work",
                table: "ChecklistTemplateItems",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ChecklistTemplateItems_TenantId_TemplateId_Position",
                schema: "work",
                table: "ChecklistTemplateItems",
                columns: new[] { "TenantId", "TemplateId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_ChecklistTemplates_TenantId",
                schema: "work",
                table: "ChecklistTemplates",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ChecklistTemplates_TenantId_Name",
                schema: "work",
                table: "ChecklistTemplates",
                columns: new[] { "TenantId", "Name" },
                unique: true,
                filter: "[IsArchived] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderAttachments_TenantId",
                schema: "work",
                table: "WorkOrderAttachments",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderAttachments_TenantId_WorkOrderId_DeletedAt",
                schema: "work",
                table: "WorkOrderAttachments",
                columns: new[] { "TenantId", "WorkOrderId", "DeletedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderAttachments_WorkOrderId",
                schema: "work",
                table: "WorkOrderAttachments",
                column: "WorkOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderChecklistItems_TenantId",
                schema: "work",
                table: "WorkOrderChecklistItems",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderChecklistItems_TenantId_WorkOrderId_Position",
                schema: "work",
                table: "WorkOrderChecklistItems",
                columns: new[] { "TenantId", "WorkOrderId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderChecklistItems_WorkOrderId",
                schema: "work",
                table: "WorkOrderChecklistItems",
                column: "WorkOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderCostLines_TenantId",
                schema: "work",
                table: "WorkOrderCostLines",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderCostLines_TenantId_WorkOrderId",
                schema: "work",
                table: "WorkOrderCostLines",
                columns: new[] { "TenantId", "WorkOrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderCostLines_WorkOrderId",
                schema: "work",
                table: "WorkOrderCostLines",
                column: "WorkOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderSignatures_TenantId",
                schema: "work",
                table: "WorkOrderSignatures",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderSignatures_TenantId_WorkOrderId",
                schema: "work",
                table: "WorkOrderSignatures",
                columns: new[] { "TenantId", "WorkOrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderSignatures_WorkOrderId",
                schema: "work",
                table: "WorkOrderSignatures",
                column: "WorkOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderTimeEntries_TenantId",
                schema: "work",
                table: "WorkOrderTimeEntries",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderTimeEntries_TenantId_WorkOrderId",
                schema: "work",
                table: "WorkOrderTimeEntries",
                columns: new[] { "TenantId", "WorkOrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderTimeEntries_WorkOrderId",
                schema: "work",
                table: "WorkOrderTimeEntries",
                column: "WorkOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderVoids_TenantId",
                schema: "work",
                table: "WorkOrderVoids",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderVoids_TenantId_EntryType_EntryId",
                schema: "work",
                table: "WorkOrderVoids",
                columns: new[] { "TenantId", "EntryType", "EntryId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderVoids_TenantId_WorkOrderId",
                schema: "work",
                table: "WorkOrderVoids",
                columns: new[] { "TenantId", "WorkOrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderVoids_WorkOrderId",
                schema: "work",
                table: "WorkOrderVoids",
                column: "WorkOrderId");

            migrationBuilder.ProtectTenantTable("ChecklistTemplates", "work");
            migrationBuilder.ProtectTenantTable("ChecklistTemplateItems", "work");
            migrationBuilder.ProtectTenantTable("WorkOrderChecklistItems", "work");
            migrationBuilder.ProtectTenantTable("WorkOrderTimeEntries", "work");
            migrationBuilder.ProtectTenantTable("WorkOrderCostLines", "work");
            migrationBuilder.ProtectTenantTable("WorkOrderVoids", "work");
            migrationBuilder.ProtectTenantTable("WorkOrderSignatures", "work");
            migrationBuilder.ProtectTenantTable("WorkOrderAttachments", "work");

            // Time, costs, voids and signatures are financial and sign-off evidence: the database itself refuses to change or remove them.
            migrationBuilder.Sql(@"CREATE TRIGGER work.TR_WorkOrderTimeEntries_AppendOnly ON work.WorkOrderTimeEntries INSTEAD OF UPDATE, DELETE AS
BEGIN
    THROW 51021, 'WorkOrderTimeEntries is append-only.', 1;
END");
            migrationBuilder.Sql(@"CREATE TRIGGER work.TR_WorkOrderCostLines_AppendOnly ON work.WorkOrderCostLines INSTEAD OF UPDATE, DELETE AS
BEGIN
    THROW 51022, 'WorkOrderCostLines is append-only.', 1;
END");
            migrationBuilder.Sql(@"CREATE TRIGGER work.TR_WorkOrderVoids_AppendOnly ON work.WorkOrderVoids INSTEAD OF UPDATE, DELETE AS
BEGIN
    THROW 51023, 'WorkOrderVoids is append-only.', 1;
END");
            migrationBuilder.Sql(@"CREATE TRIGGER work.TR_WorkOrderSignatures_AppendOnly ON work.WorkOrderSignatures INSTEAD OF UPDATE, DELETE AS
BEGIN
    THROW 51024, 'WorkOrderSignatures is append-only.', 1;
END");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS work.TR_WorkOrderTimeEntries_AppendOnly;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS work.TR_WorkOrderCostLines_AppendOnly;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS work.TR_WorkOrderVoids_AppendOnly;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS work.TR_WorkOrderSignatures_AppendOnly;");
            migrationBuilder.UnprotectTenantTable("WorkOrderAttachments", "work");
            migrationBuilder.UnprotectTenantTable("WorkOrderSignatures", "work");
            migrationBuilder.UnprotectTenantTable("WorkOrderVoids", "work");
            migrationBuilder.UnprotectTenantTable("WorkOrderCostLines", "work");
            migrationBuilder.UnprotectTenantTable("WorkOrderTimeEntries", "work");
            migrationBuilder.UnprotectTenantTable("WorkOrderChecklistItems", "work");
            migrationBuilder.UnprotectTenantTable("ChecklistTemplateItems", "work");
            migrationBuilder.UnprotectTenantTable("ChecklistTemplates", "work");

            migrationBuilder.DropTable(
                name: "ChecklistTemplateItems",
                schema: "work");

            migrationBuilder.DropTable(
                name: "WorkOrderAttachments",
                schema: "work");

            migrationBuilder.DropTable(
                name: "WorkOrderChecklistItems",
                schema: "work");

            migrationBuilder.DropTable(
                name: "WorkOrderCostLines",
                schema: "work");

            migrationBuilder.DropTable(
                name: "WorkOrderSignatures",
                schema: "work");

            migrationBuilder.DropTable(
                name: "WorkOrderTimeEntries",
                schema: "work");

            migrationBuilder.DropTable(
                name: "WorkOrderVoids",
                schema: "work");

            migrationBuilder.DropTable(
                name: "ChecklistTemplates",
                schema: "work");

            migrationBuilder.DropColumn(
                name: "CurrencyCode",
                schema: "work",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "LabourRatePerHour",
                schema: "work",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "RequireCompletionSignature",
                schema: "work",
                table: "Settings");
        }
    }
}
