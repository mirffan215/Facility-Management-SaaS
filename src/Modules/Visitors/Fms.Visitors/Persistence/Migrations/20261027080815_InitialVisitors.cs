using System;
using Fms.BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fms.Visitors.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialVisitors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "visitors");

            migrationBuilder.CreateTable(
                name: "Settings",
                schema: "visitors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequireInduction = table.Column<bool>(type: "bit", nullable: false),
                    InductionText = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    RetentionDays = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Id);
                    table.CheckConstraint("CK_Settings_Retention", "[RetentionDays] BETWEEN 30 AND 3650");
                });

            migrationBuilder.CreateTable(
                name: "Visits",
                schema: "visitors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HostId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HostName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SiteName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    VisitorName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    VisitorCompany = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    VisitorEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    VisitorPhone = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Purpose = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ExpectedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpectedUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    PassHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CancelledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancelReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CheckedInAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CheckedInBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BadgeNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    InductionAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    WatchlistMatch = table.Column<bool>(type: "bit", nullable: false),
                    OverrideReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    OverrideBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CheckedOutAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CheckedOutBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AnonymisedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Visits", x => x.Id);
                    table.CheckConstraint("CK_Visits_Times", "[ExpectedUntil] IS NULL OR [ExpectedUntil] >= [ExpectedAt]");
                });

            migrationBuilder.CreateTable(
                name: "Watchlist",
                schema: "visitors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NameKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    AddedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AddedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RemovedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RemovedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RemoveReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Watchlist", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Settings_TenantId",
                schema: "visitors",
                table: "Settings",
                column: "TenantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Visits_PassHash",
                schema: "visitors",
                table: "Visits",
                column: "PassHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Visits_TenantId_HostId_ExpectedAt",
                schema: "visitors",
                table: "Visits",
                columns: new[] { "TenantId", "HostId", "ExpectedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Visits_TenantId_SiteId_Status",
                schema: "visitors",
                table: "Visits",
                columns: new[] { "TenantId", "SiteId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Visits_TenantId_Status_ExpectedAt",
                schema: "visitors",
                table: "Visits",
                columns: new[] { "TenantId", "Status", "ExpectedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Watchlist_TenantId_NameKey",
                schema: "visitors",
                table: "Watchlist",
                columns: new[] { "TenantId", "NameKey" });

            migrationBuilder.ProtectTenantTable("Visits", "visitors");
            migrationBuilder.ProtectTenantTable("Watchlist", "visitors");
            migrationBuilder.ProtectTenantTable("Settings", "visitors");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Settings",
                schema: "visitors");

            migrationBuilder.DropTable(
                name: "Visits",
                schema: "visitors");

            migrationBuilder.DropTable(
                name: "Watchlist",
                schema: "visitors");
        }
    }
}
