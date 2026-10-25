using System;
using Fms.BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fms.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Outbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AvailableAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    LockedUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LockedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeadLetteredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OutboxMessages_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_ProcessedAt_DeadLetteredAt_AvailableAt",
                table: "OutboxMessages",
                columns: new[] { "ProcessedAt", "DeadLetteredAt", "AvailableAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_TenantId",
                table: "OutboxMessages",
                column: "TenantId");

            migrationBuilder.ProtectTenantTable("OutboxMessages");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UnprotectTenantTable("OutboxMessages");

            migrationBuilder.DropTable(
                name: "OutboxMessages");
        }
    }
}
