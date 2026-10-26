using System;
using Fms.BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fms.Inventory.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "inventory");

            migrationBuilder.CreateTable(
                name: "Items",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Unit = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Barcode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Items", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PmTemplateParts",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PmTemplateId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    StoreId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PmTemplateParts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Reservations",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StoreId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reservations", x => x.Id);
                    table.CheckConstraint("CK_Reservations_Quantity", "[Quantity] >= 0");
                });

            migrationBuilder.CreateTable(
                name: "StockLevels",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StoreId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OnHand = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    Reserved = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    AverageCost = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    MinQty = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    MaxQty = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    LowNotified = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockLevels", x => x.Id);
                    table.CheckConstraint("CK_StockLevels_Quantities", "[OnHand] >= 0 AND [Reserved] >= 0 AND [Reserved] <= [OnHand]");
                });

            migrationBuilder.CreateTable(
                name: "StockMovements",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StoreId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    UnitCost = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    BalanceAfter = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    By = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    WorkOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TransferId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StockTakeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Reference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockMovements", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StockTakeLines",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StockTakeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SystemQty = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    UnitCost = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    CountedQty = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockTakeLines", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StockTakes",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StoreId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    StartedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SubmittedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DecidedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockTakes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Stores",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Stores", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Items_TenantId",
                schema: "inventory",
                table: "Items",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Items_TenantId_Barcode",
                schema: "inventory",
                table: "Items",
                columns: new[] { "TenantId", "Barcode" },
                unique: true,
                filter: "[Barcode] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Items_TenantId_Code",
                schema: "inventory",
                table: "Items",
                columns: new[] { "TenantId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PmTemplateParts_TenantId",
                schema: "inventory",
                table: "PmTemplateParts",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PmTemplateParts_TenantId_PmTemplateId_ItemId",
                schema: "inventory",
                table: "PmTemplateParts",
                columns: new[] { "TenantId", "PmTemplateId", "ItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Reservations_TenantId",
                schema: "inventory",
                table: "Reservations",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Reservations_TenantId_StoreId_ItemId",
                schema: "inventory",
                table: "Reservations",
                columns: new[] { "TenantId", "StoreId", "ItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_Reservations_TenantId_WorkOrderId_StoreId_ItemId",
                schema: "inventory",
                table: "Reservations",
                columns: new[] { "TenantId", "WorkOrderId", "StoreId", "ItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockLevels_TenantId",
                schema: "inventory",
                table: "StockLevels",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_StockLevels_TenantId_ItemId",
                schema: "inventory",
                table: "StockLevels",
                columns: new[] { "TenantId", "ItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_StockLevels_TenantId_StoreId_ItemId",
                schema: "inventory",
                table: "StockLevels",
                columns: new[] { "TenantId", "StoreId", "ItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_TenantId",
                schema: "inventory",
                table: "StockMovements",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_TenantId_StoreId_ItemId_At",
                schema: "inventory",
                table: "StockMovements",
                columns: new[] { "TenantId", "StoreId", "ItemId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_TenantId_WorkOrderId",
                schema: "inventory",
                table: "StockMovements",
                columns: new[] { "TenantId", "WorkOrderId" },
                filter: "[WorkOrderId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_StockTakeLines_TenantId",
                schema: "inventory",
                table: "StockTakeLines",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_StockTakeLines_TenantId_StockTakeId_ItemId",
                schema: "inventory",
                table: "StockTakeLines",
                columns: new[] { "TenantId", "StockTakeId", "ItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockTakes_TenantId",
                schema: "inventory",
                table: "StockTakes",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_StockTakes_TenantId_StoreId_Status",
                schema: "inventory",
                table: "StockTakes",
                columns: new[] { "TenantId", "StoreId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Stores_TenantId",
                schema: "inventory",
                table: "Stores",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Stores_TenantId_Code",
                schema: "inventory",
                table: "Stores",
                columns: new[] { "TenantId", "Code" },
                unique: true);
        
            migrationBuilder.ProtectTenantTable("Stores", "inventory");
            migrationBuilder.ProtectTenantTable("Items", "inventory");
            migrationBuilder.ProtectTenantTable("StockLevels", "inventory");
            migrationBuilder.ProtectTenantTable("StockMovements", "inventory");
            migrationBuilder.ProtectTenantTable("Reservations", "inventory");
            migrationBuilder.ProtectTenantTable("PmTemplateParts", "inventory");
            migrationBuilder.ProtectTenantTable("StockTakes", "inventory");
            migrationBuilder.ProtectTenantTable("StockTakeLines", "inventory");

            // The ledger of stock movements is evidence: the database itself refuses to change or remove a row.
            migrationBuilder.Sql(@"CREATE TRIGGER inventory.TR_StockMovements_AppendOnly ON inventory.StockMovements INSTEAD OF UPDATE, DELETE AS
BEGIN
    THROW 51031, 'StockMovements is append-only.', 1;
END");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS inventory.TR_StockMovements_AppendOnly;");
            migrationBuilder.UnprotectTenantTable("StockTakeLines", "inventory");
            migrationBuilder.UnprotectTenantTable("StockTakes", "inventory");
            migrationBuilder.UnprotectTenantTable("PmTemplateParts", "inventory");
            migrationBuilder.UnprotectTenantTable("Reservations", "inventory");
            migrationBuilder.UnprotectTenantTable("StockMovements", "inventory");
            migrationBuilder.UnprotectTenantTable("StockLevels", "inventory");
            migrationBuilder.UnprotectTenantTable("Items", "inventory");
            migrationBuilder.UnprotectTenantTable("Stores", "inventory");

            migrationBuilder.DropTable(
                name: "Items",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "PmTemplateParts",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "Reservations",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "StockLevels",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "StockMovements",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "StockTakeLines",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "StockTakes",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "Stores",
                schema: "inventory");
        }
    }
}
