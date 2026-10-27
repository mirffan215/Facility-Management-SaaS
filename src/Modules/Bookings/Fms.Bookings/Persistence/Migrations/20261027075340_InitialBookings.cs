using System;
using Fms.BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fms.Bookings.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialBookings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "bookings");

            migrationBuilder.CreateTable(
                name: "Bookings",
                schema: "bookings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    StartsAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EndsAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Attendees = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    SeriesId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    DecidedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DecisionNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CancelledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancelledBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CancelReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CheckedInAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Bookings", x => x.Id);
                    table.CheckConstraint("CK_Bookings_Times", "[EndsAt] > [StartsAt]");
                });

            migrationBuilder.CreateTable(
                name: "CalendarFeedTokens",
                schema: "bookings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarFeedTokens", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CalendarLinks",
                schema: "bookings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BookingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExternalEventId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarLinks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CalendarSyncSettings",
                schema: "bookings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    Adapter = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarSyncSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Resources",
                schema: "bookings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SpaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Capacity = table.Column<int>(type: "int", nullable: false),
                    TimeZone = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    OpensAt = table.Column<TimeOnly>(type: "time", nullable: false),
                    ClosesAt = table.Column<TimeOnly>(type: "time", nullable: false),
                    DaysMask = table.Column<int>(type: "int", nullable: false),
                    MinMinutes = table.Column<int>(type: "int", nullable: false),
                    MaxMinutes = table.Column<int>(type: "int", nullable: false),
                    MaxAdvanceDays = table.Column<int>(type: "int", nullable: false),
                    BufferMinutes = table.Column<int>(type: "int", nullable: false),
                    RequiresApproval = table.Column<bool>(type: "bit", nullable: false),
                    CheckInRequired = table.Column<bool>(type: "bit", nullable: false),
                    CheckInGraceMinutes = table.Column<int>(type: "int", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Resources", x => x.Id);
                    table.CheckConstraint("CK_Resources_Capacity", "[Capacity] BETWEEN 1 AND 10000");
                    table.CheckConstraint("CK_Resources_Days", "[DaysMask] BETWEEN 1 AND 127");
                    table.CheckConstraint("CK_Resources_Durations", "[MinMinutes] >= 5 AND [MaxMinutes] >= [MinMinutes] AND [MaxMinutes] <= 1440 AND [BufferMinutes] BETWEEN 0 AND 480");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_TenantId_OrganizerId_StartsAt",
                schema: "bookings",
                table: "Bookings",
                columns: new[] { "TenantId", "OrganizerId", "StartsAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_TenantId_ResourceId_StartsAt",
                schema: "bookings",
                table: "Bookings",
                columns: new[] { "TenantId", "ResourceId", "StartsAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_TenantId_Status_StartsAt",
                schema: "bookings",
                table: "Bookings",
                columns: new[] { "TenantId", "Status", "StartsAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarFeedTokens_TenantId_UserId",
                schema: "bookings",
                table: "CalendarFeedTokens",
                columns: new[] { "TenantId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CalendarFeedTokens_TokenHash",
                schema: "bookings",
                table: "CalendarFeedTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CalendarLinks_TenantId_BookingId",
                schema: "bookings",
                table: "CalendarLinks",
                columns: new[] { "TenantId", "BookingId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CalendarSyncSettings_TenantId",
                schema: "bookings",
                table: "CalendarSyncSettings",
                column: "TenantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Resources_TenantId_Code",
                schema: "bookings",
                table: "Resources",
                columns: new[] { "TenantId", "Code" },
                unique: true);

            migrationBuilder.ProtectTenantTable("Resources", "bookings");
            migrationBuilder.ProtectTenantTable("Bookings", "bookings");
            migrationBuilder.ProtectTenantTable("CalendarLinks", "bookings");
            migrationBuilder.ProtectTenantTable("CalendarFeedTokens", "bookings");
            migrationBuilder.ProtectTenantTable("CalendarSyncSettings", "bookings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Bookings",
                schema: "bookings");

            migrationBuilder.DropTable(
                name: "CalendarFeedTokens",
                schema: "bookings");

            migrationBuilder.DropTable(
                name: "CalendarLinks",
                schema: "bookings");

            migrationBuilder.DropTable(
                name: "CalendarSyncSettings",
                schema: "bookings");

            migrationBuilder.DropTable(
                name: "Resources",
                schema: "bookings");
        }
    }
}
