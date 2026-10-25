using System;
using Fms.BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fms.Facilities.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LocationsAndCustomFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "facilities");

            migrationBuilder.CreateTable(
                name: "AssetClasses",
                schema: "facilities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssetClasses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssetClasses_AssetClasses_ParentId",
                        column: x => x.ParentId,
                        principalSchema: "facilities",
                        principalTable: "AssetClasses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Locations",
                schema: "facilities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Type = table.Column<int>(type: "int", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Path = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Address = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TimeZone = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    AreaSquareMetres = table.Column<decimal>(type: "decimal(12,2)", precision: 12, scale: 2, nullable: true),
                    CustomFieldsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Locations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Locations_Locations_ParentId",
                        column: x => x.ParentId,
                        principalSchema: "facilities",
                        principalTable: "Locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomFieldDefinitions",
                schema: "facilities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Target = table.Column<int>(type: "int", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    LabelEn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    LabelAr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    DataType = table.Column<int>(type: "int", nullable: false),
                    IsRequired = table.Column<bool>(type: "bit", nullable: false),
                    ChoicesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AssetClassId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomFieldDefinitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomFieldDefinitions_AssetClasses_AssetClassId",
                        column: x => x.AssetClassId,
                        principalSchema: "facilities",
                        principalTable: "AssetClasses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssetClasses_ParentId",
                schema: "facilities",
                table: "AssetClasses",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_AssetClasses_TenantId",
                schema: "facilities",
                table: "AssetClasses",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_AssetClasses_TenantId_Code",
                schema: "facilities",
                table: "AssetClasses",
                columns: new[] { "TenantId", "Code" },
                unique: true,
                filter: "[IsArchived] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_CustomFieldDefinitions_AssetClassId",
                schema: "facilities",
                table: "CustomFieldDefinitions",
                column: "AssetClassId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomFieldDefinitions_TenantId",
                schema: "facilities",
                table: "CustomFieldDefinitions",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomFieldDefinitions_TenantId_Target_Key",
                schema: "facilities",
                table: "CustomFieldDefinitions",
                columns: new[] { "TenantId", "Target", "Key" },
                unique: true,
                filter: "[IsArchived] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_Locations_ParentId",
                schema: "facilities",
                table: "Locations",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_Locations_TenantId",
                schema: "facilities",
                table: "Locations",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Locations_TenantId_Code",
                schema: "facilities",
                table: "Locations",
                columns: new[] { "TenantId", "Code" },
                unique: true,
                filter: "[IsArchived] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_Locations_TenantId_ParentId",
                schema: "facilities",
                table: "Locations",
                columns: new[] { "TenantId", "ParentId" });

            migrationBuilder.CreateIndex(
                name: "IX_Locations_TenantId_Path",
                schema: "facilities",
                table: "Locations",
                columns: new[] { "TenantId", "Path" });

            migrationBuilder.ProtectTenantTable("Locations", "facilities");
            migrationBuilder.ProtectTenantTable("CustomFieldDefinitions", "facilities");
            migrationBuilder.ProtectTenantTable("AssetClasses", "facilities");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UnprotectTenantTable("AssetClasses", "facilities");
            migrationBuilder.UnprotectTenantTable("CustomFieldDefinitions", "facilities");
            migrationBuilder.UnprotectTenantTable("Locations", "facilities");

            migrationBuilder.DropTable(
                name: "CustomFieldDefinitions",
                schema: "facilities");

            migrationBuilder.DropTable(
                name: "Locations",
                schema: "facilities");

            migrationBuilder.DropTable(
                name: "AssetClasses",
                schema: "facilities");
        }
    }
}
