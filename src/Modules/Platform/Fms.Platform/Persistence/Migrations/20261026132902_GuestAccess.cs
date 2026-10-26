using System;
using Fms.BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fms.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GuestAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GuestIdentities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvitationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuestIdentities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GuestIdentities_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GuestInvitations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    TargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AcceptedProvider = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    AcceptedSubject = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AcceptedUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuestInvitations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GuestIdentities_Provider_Subject",
                table: "GuestIdentities",
                columns: new[] { "Provider", "Subject" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GuestIdentities_TenantId",
                table: "GuestIdentities",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_GuestInvitations_TenantId",
                table: "GuestInvitations",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_GuestInvitations_TenantId_Kind_TargetId",
                table: "GuestInvitations",
                columns: new[] { "TenantId", "Kind", "TargetId" });

            migrationBuilder.CreateIndex(
                name: "IX_GuestInvitations_TokenHash",
                table: "GuestInvitations",
                column: "TokenHash",
                unique: true);
        
            migrationBuilder.ProtectTenantTable("GuestInvitations");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UnprotectTenantTable("GuestInvitations");

            migrationBuilder.DropTable(
                name: "GuestIdentities");

            migrationBuilder.DropTable(
                name: "GuestInvitations");
        }
    }
}
