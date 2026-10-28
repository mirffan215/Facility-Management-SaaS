using System;
using Fms.BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fms.Ai.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Feedback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Feedback",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UsageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Feature = table.Column<int>(type: "int", nullable: false),
                    Accepted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Feedback", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Feedback_TenantId",
                schema: "ai",
                table: "Feedback",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Feedback_TenantId_At",
                schema: "ai",
                table: "Feedback",
                columns: new[] { "TenantId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_Feedback_TenantId_UsageId",
                schema: "ai",
                table: "Feedback",
                columns: new[] { "TenantId", "UsageId" },
                unique: true);
            migrationBuilder.ProtectTenantTable("Feedback", "ai");

            migrationBuilder.Sql(@"CREATE TRIGGER ai.TR_Feedback_AppendOnly ON ai.Feedback INSTEAD OF UPDATE, DELETE AS
BEGIN
    THROW 51062, 'Feedback is append-only.', 1;
END");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Feedback",
                schema: "ai");
        }
    }
}
