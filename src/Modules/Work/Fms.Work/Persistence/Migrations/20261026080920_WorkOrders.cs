using System;
using Fms.BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fms.Work.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "work");

            migrationBuilder.CreateTable(
                name: "Categories",
                schema: "work",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    DefaultPriority = table.Column<int>(type: "int", nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Categories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                schema: "work",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequireApproval = table.Column<bool>(type: "bit", nullable: false),
                    RequireVerification = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkOrders",
                schema: "work",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Type = table.Column<int>(type: "int", nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    LocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RequestedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AssignedTo = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DueAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    VerifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    VerifiedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ClosedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    StatusReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ClosureCode = table.Column<int>(type: "int", nullable: true),
                    ClosureNotes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkOrders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkOrders_Categories_CategoryId",
                        column: x => x.CategoryId,
                        principalSchema: "work",
                        principalTable: "Categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkOrderComments",
                schema: "work",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AuthorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Body = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    IsInternal = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkOrderComments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkOrderComments_WorkOrders_WorkOrderId",
                        column: x => x.WorkOrderId,
                        principalSchema: "work",
                        principalTable: "WorkOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkOrderEvents",
                schema: "work",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Action = table.Column<int>(type: "int", nullable: false),
                    FromStatus = table.Column<int>(type: "int", nullable: true),
                    ToStatus = table.Column<int>(type: "int", nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    SubjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkOrderEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkOrderEvents_WorkOrders_WorkOrderId",
                        column: x => x.WorkOrderId,
                        principalSchema: "work",
                        principalTable: "WorkOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Categories_TenantId",
                schema: "work",
                table: "Categories",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Categories_TenantId_Code",
                schema: "work",
                table: "Categories",
                columns: new[] { "TenantId", "Code" },
                unique: true,
                filter: "[IsArchived] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_Settings_TenantId",
                schema: "work",
                table: "Settings",
                column: "TenantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderComments_TenantId",
                schema: "work",
                table: "WorkOrderComments",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderComments_TenantId_WorkOrderId_At",
                schema: "work",
                table: "WorkOrderComments",
                columns: new[] { "TenantId", "WorkOrderId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderComments_WorkOrderId",
                schema: "work",
                table: "WorkOrderComments",
                column: "WorkOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderEvents_TenantId",
                schema: "work",
                table: "WorkOrderEvents",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderEvents_TenantId_WorkOrderId_At",
                schema: "work",
                table: "WorkOrderEvents",
                columns: new[] { "TenantId", "WorkOrderId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderEvents_WorkOrderId",
                schema: "work",
                table: "WorkOrderEvents",
                column: "WorkOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_CategoryId",
                schema: "work",
                table: "WorkOrders",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_TenantId",
                schema: "work",
                table: "WorkOrders",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_TenantId_AssetId",
                schema: "work",
                table: "WorkOrders",
                columns: new[] { "TenantId", "AssetId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_TenantId_AssignedTo",
                schema: "work",
                table: "WorkOrders",
                columns: new[] { "TenantId", "AssignedTo" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_TenantId_LocationId",
                schema: "work",
                table: "WorkOrders",
                columns: new[] { "TenantId", "LocationId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_TenantId_Number",
                schema: "work",
                table: "WorkOrders",
                columns: new[] { "TenantId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_TenantId_RequestedBy",
                schema: "work",
                table: "WorkOrders",
                columns: new[] { "TenantId", "RequestedBy" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_TenantId_Status",
                schema: "work",
                table: "WorkOrders",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.ProtectTenantTable("Categories", "work");
            migrationBuilder.ProtectTenantTable("Settings", "work");
            migrationBuilder.ProtectTenantTable("WorkOrders", "work");
            migrationBuilder.ProtectTenantTable("WorkOrderEvents", "work");
            migrationBuilder.ProtectTenantTable("WorkOrderComments", "work");

            // History and comments are evidence: the database itself refuses to change or remove them.
            migrationBuilder.Sql(@"CREATE TRIGGER work.TR_WorkOrderEvents_AppendOnly ON work.WorkOrderEvents INSTEAD OF UPDATE, DELETE AS
BEGIN
    THROW 51011, 'WorkOrderEvents is append-only.', 1;
END");
            migrationBuilder.Sql(@"CREATE TRIGGER work.TR_WorkOrderComments_AppendOnly ON work.WorkOrderComments INSTEAD OF UPDATE, DELETE AS
BEGIN
    THROW 51012, 'WorkOrderComments is append-only.', 1;
END");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS work.TR_WorkOrderComments_AppendOnly;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS work.TR_WorkOrderEvents_AppendOnly;");
            migrationBuilder.UnprotectTenantTable("WorkOrderComments", "work");
            migrationBuilder.UnprotectTenantTable("WorkOrderEvents", "work");
            migrationBuilder.UnprotectTenantTable("WorkOrders", "work");
            migrationBuilder.UnprotectTenantTable("Settings", "work");
            migrationBuilder.UnprotectTenantTable("Categories", "work");

            migrationBuilder.DropTable(
                name: "Settings",
                schema: "work");

            migrationBuilder.DropTable(
                name: "WorkOrderComments",
                schema: "work");

            migrationBuilder.DropTable(
                name: "WorkOrderEvents",
                schema: "work");

            migrationBuilder.DropTable(
                name: "WorkOrders",
                schema: "work");

            migrationBuilder.DropTable(
                name: "Categories",
                schema: "work");
        }
    }
}
