using System;
using Fms.BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fms.Compliance.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCompliance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "compliance");

            migrationBuilder.CreateTable(
                name: "Completions",
                schema: "compliance",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ObligationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PerformedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    ValidUntil = table.Column<DateOnly>(type: "date", nullable: true),
                    Result = table.Column<int>(type: "int", nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ProviderName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RecordedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RecordedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RecordedByName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Completions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Documents",
                schema: "compliance",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompletionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    StorageKey = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    UploadedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UploadedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeletedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeleteReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Documents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Obligations",
                schema: "compliance",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Category = table.Column<int>(type: "int", nullable: false),
                    Authority = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    LocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LocationName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AssetName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IntervalMonths = table.Column<int>(type: "int", nullable: true),
                    LeadDays = table.Column<int>(type: "int", nullable: false),
                    FirstDueOn = table.Column<DateOnly>(type: "date", nullable: true),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OwnerName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    LastCompletedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    ValidUntil = table.Column<DateOnly>(type: "date", nullable: true),
                    LastResult = table.Column<int>(type: "int", nullable: true),
                    LastAlertStatus = table.Column<int>(type: "int", nullable: true),
                    LastAlertOn = table.Column<DateOnly>(type: "date", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Obligations", x => x.Id);
                    table.CheckConstraint("CK_Obligations_Interval", "[IntervalMonths] IS NULL OR [IntervalMonths] BETWEEN 1 AND 240");
                    table.CheckConstraint("CK_Obligations_Lead", "[LeadDays] BETWEEN 0 AND 365");
                });

            migrationBuilder.CreateTable(
                name: "PermitEvents",
                schema: "compliance",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PermitId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ByName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PermitEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Permits",
                schema: "compliance",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<int>(type: "int", nullable: false),
                    TypeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TypeCode = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    TypeName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TypeExclusive = table.Column<bool>(type: "bit", nullable: false),
                    TypeRequiresIsolation = table.Column<bool>(type: "bit", nullable: false),
                    WorkOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    WorkOrderNumber = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    LocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LocationName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    ContractorName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PlannedStart = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PlannedEnd = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    PrecautionsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RequestedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestedByName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ApprovedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ApprovedByName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RejectReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IssuedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IssuedByName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IssuedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    IsolationReference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    StartedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StartedByName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ClosedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ClosedByName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ClosedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ClosureNote = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    AreaSafe = table.Column<bool>(type: "bit", nullable: true),
                    CancelReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Permits", x => x.Id);
                    table.CheckConstraint("CK_Permits_Times", "[PlannedEnd] > [PlannedStart]");
                });

            migrationBuilder.CreateTable(
                name: "PermitTypes",
                schema: "compliance",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    MaxDurationHours = table.Column<int>(type: "int", nullable: false),
                    Exclusive = table.Column<bool>(type: "bit", nullable: false),
                    RequiresIsolation = table.Column<bool>(type: "bit", nullable: false),
                    PrecautionsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PermitTypes", x => x.Id);
                    table.CheckConstraint("CK_PermitTypes_Duration", "[MaxDurationHours] BETWEEN 1 AND 720");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Completions_TenantId",
                schema: "compliance",
                table: "Completions",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Completions_TenantId_ObligationId_PerformedOn",
                schema: "compliance",
                table: "Completions",
                columns: new[] { "TenantId", "ObligationId", "PerformedOn" });

            migrationBuilder.CreateIndex(
                name: "IX_Documents_TenantId",
                schema: "compliance",
                table: "Documents",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_TenantId_CompletionId",
                schema: "compliance",
                table: "Documents",
                columns: new[] { "TenantId", "CompletionId" });

            migrationBuilder.CreateIndex(
                name: "IX_Obligations_TenantId",
                schema: "compliance",
                table: "Obligations",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Obligations_TenantId_IsArchived_ValidUntil",
                schema: "compliance",
                table: "Obligations",
                columns: new[] { "TenantId", "IsArchived", "ValidUntil" });

            migrationBuilder.CreateIndex(
                name: "IX_Obligations_TenantId_LocationId",
                schema: "compliance",
                table: "Obligations",
                columns: new[] { "TenantId", "LocationId" });

            migrationBuilder.CreateIndex(
                name: "IX_PermitEvents_TenantId",
                schema: "compliance",
                table: "PermitEvents",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PermitEvents_TenantId_PermitId_At",
                schema: "compliance",
                table: "PermitEvents",
                columns: new[] { "TenantId", "PermitId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_Permits_TenantId",
                schema: "compliance",
                table: "Permits",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Permits_TenantId_LocationId_Status",
                schema: "compliance",
                table: "Permits",
                columns: new[] { "TenantId", "LocationId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Permits_TenantId_Number",
                schema: "compliance",
                table: "Permits",
                columns: new[] { "TenantId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Permits_TenantId_RequestedBy",
                schema: "compliance",
                table: "Permits",
                columns: new[] { "TenantId", "RequestedBy" });

            migrationBuilder.CreateIndex(
                name: "IX_Permits_TenantId_Status_PlannedStart",
                schema: "compliance",
                table: "Permits",
                columns: new[] { "TenantId", "Status", "PlannedStart" });

            migrationBuilder.CreateIndex(
                name: "IX_PermitTypes_TenantId",
                schema: "compliance",
                table: "PermitTypes",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PermitTypes_TenantId_Code",
                schema: "compliance",
                table: "PermitTypes",
                columns: new[] { "TenantId", "Code" },
                unique: true);
            migrationBuilder.ProtectTenantTable("Obligations", "compliance");
            migrationBuilder.ProtectTenantTable("Completions", "compliance");
            migrationBuilder.ProtectTenantTable("Documents", "compliance");
            migrationBuilder.ProtectTenantTable("PermitTypes", "compliance");
            migrationBuilder.ProtectTenantTable("Permits", "compliance");
            migrationBuilder.ProtectTenantTable("PermitEvents", "compliance");

            migrationBuilder.Sql(@"CREATE TRIGGER compliance.TR_Completions_AppendOnly ON compliance.Completions INSTEAD OF UPDATE, DELETE AS
BEGIN
    THROW 51041, 'Completions is append-only.', 1;
END");
            migrationBuilder.Sql(@"CREATE TRIGGER compliance.TR_PermitEvents_AppendOnly ON compliance.PermitEvents INSTEAD OF UPDATE, DELETE AS
BEGIN
    THROW 51042, 'PermitEvents is append-only.', 1;
END");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Completions",
                schema: "compliance");

            migrationBuilder.DropTable(
                name: "Documents",
                schema: "compliance");

            migrationBuilder.DropTable(
                name: "Obligations",
                schema: "compliance");

            migrationBuilder.DropTable(
                name: "PermitEvents",
                schema: "compliance");

            migrationBuilder.DropTable(
                name: "Permits",
                schema: "compliance");

            migrationBuilder.DropTable(
                name: "PermitTypes",
                schema: "compliance");
        }
    }
}
