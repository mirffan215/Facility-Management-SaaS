using System;
using Fms.BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fms.Vendors.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialVendors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "vendors");

            migrationBuilder.CreateTable(
                name: "Contacts",
                schema: "vendors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VendorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Role = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    Phone = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    IsPrimary = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Contacts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ContractCoverage",
                schema: "vendors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContractId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    TargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContractCoverage", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ContractDocuments",
                schema: "vendors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContractId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    StorageKey = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    UploadedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UploadedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeletedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeleteReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContractDocuments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Contracts",
                schema: "vendors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VendorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    StartOn = table.Column<DateOnly>(type: "date", nullable: false),
                    EndOn = table.Column<DateOnly>(type: "date", nullable: false),
                    Value = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    CurrencyCode = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    RenewalNoticeDays = table.Column<int>(type: "int", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    TerminatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    TerminationReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ExpiringNoticedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ExpiredNoticedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Contracts", x => x.Id);
                    table.CheckConstraint("CK_Contracts_Dates", "[EndOn] >= [StartOn]");
                });

            migrationBuilder.CreateTable(
                name: "ContractSla",
                schema: "vendors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContractId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    ResponseMinutes = table.Column<int>(type: "int", nullable: false),
                    ResolutionMinutes = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContractSla", x => x.Id);
                    table.CheckConstraint("CK_ContractSla_Values", "[Priority] BETWEEN 1 AND 4 AND [ResponseMinutes] > 0 AND [ResolutionMinutes] >= [ResponseMinutes]");
                });

            migrationBuilder.CreateTable(
                name: "Vendors",
                schema: "vendors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    StatusReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TaxNumber = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    RegistrationNumber = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    Phone = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Address = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Country = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: true),
                    PaymentTerms = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ErpAccount = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Vendors", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_TenantId",
                schema: "vendors",
                table: "Contacts",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_TenantId_VendorId",
                schema: "vendors",
                table: "Contacts",
                columns: new[] { "TenantId", "VendorId" });

            migrationBuilder.CreateIndex(
                name: "IX_ContractCoverage_TenantId",
                schema: "vendors",
                table: "ContractCoverage",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ContractCoverage_TenantId_ContractId_Kind_TargetId",
                schema: "vendors",
                table: "ContractCoverage",
                columns: new[] { "TenantId", "ContractId", "Kind", "TargetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ContractDocuments_TenantId",
                schema: "vendors",
                table: "ContractDocuments",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ContractDocuments_TenantId_ContractId",
                schema: "vendors",
                table: "ContractDocuments",
                columns: new[] { "TenantId", "ContractId" });

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_TenantId",
                schema: "vendors",
                table: "Contracts",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_TenantId_EndOn",
                schema: "vendors",
                table: "Contracts",
                columns: new[] { "TenantId", "EndOn" });

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_TenantId_Number",
                schema: "vendors",
                table: "Contracts",
                columns: new[] { "TenantId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_TenantId_VendorId",
                schema: "vendors",
                table: "Contracts",
                columns: new[] { "TenantId", "VendorId" });

            migrationBuilder.CreateIndex(
                name: "IX_ContractSla_TenantId",
                schema: "vendors",
                table: "ContractSla",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ContractSla_TenantId_ContractId_Priority",
                schema: "vendors",
                table: "ContractSla",
                columns: new[] { "TenantId", "ContractId", "Priority" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Vendors_TenantId",
                schema: "vendors",
                table: "Vendors",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Vendors_TenantId_Code",
                schema: "vendors",
                table: "Vendors",
                columns: new[] { "TenantId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Vendors_TenantId_TaxNumber",
                schema: "vendors",
                table: "Vendors",
                columns: new[] { "TenantId", "TaxNumber" },
                unique: true,
                filter: "[TaxNumber] IS NOT NULL");
        
            migrationBuilder.ProtectTenantTable("Vendors", "vendors");
            migrationBuilder.ProtectTenantTable("Contacts", "vendors");
            migrationBuilder.ProtectTenantTable("Contracts", "vendors");
            migrationBuilder.ProtectTenantTable("ContractCoverage", "vendors");
            migrationBuilder.ProtectTenantTable("ContractSla", "vendors");
            migrationBuilder.ProtectTenantTable("ContractDocuments", "vendors");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UnprotectTenantTable("ContractDocuments", "vendors");
            migrationBuilder.UnprotectTenantTable("ContractSla", "vendors");
            migrationBuilder.UnprotectTenantTable("ContractCoverage", "vendors");
            migrationBuilder.UnprotectTenantTable("Contracts", "vendors");
            migrationBuilder.UnprotectTenantTable("Contacts", "vendors");
            migrationBuilder.UnprotectTenantTable("Vendors", "vendors");

            migrationBuilder.DropTable(
                name: "Contacts",
                schema: "vendors");

            migrationBuilder.DropTable(
                name: "ContractCoverage",
                schema: "vendors");

            migrationBuilder.DropTable(
                name: "ContractDocuments",
                schema: "vendors");

            migrationBuilder.DropTable(
                name: "Contracts",
                schema: "vendors");

            migrationBuilder.DropTable(
                name: "ContractSla",
                schema: "vendors");

            migrationBuilder.DropTable(
                name: "Vendors",
                schema: "vendors");
        }
    }
}
