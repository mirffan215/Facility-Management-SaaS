using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fms.Work.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HintThresholds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "HintHistoryDays",
                schema: "work",
                table: "Settings",
                type: "int",
                nullable: false,
                defaultValue: 180);

            migrationBuilder.AddColumn<int>(
                name: "HintMinFailures",
                schema: "work",
                table: "Settings",
                type: "int",
                nullable: false,
                defaultValue: 4);

            migrationBuilder.AddColumn<int>(
                name: "HintPaceRisePercent",
                schema: "work",
                table: "Settings",
                type: "int",
                nullable: false,
                defaultValue: 150);

            migrationBuilder.AddColumn<int>(
                name: "HintShrinkPercent",
                schema: "work",
                table: "Settings",
                type: "int",
                nullable: false,
                defaultValue: 50);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HintHistoryDays",
                schema: "work",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "HintMinFailures",
                schema: "work",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "HintPaceRisePercent",
                schema: "work",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "HintShrinkPercent",
                schema: "work",
                table: "Settings");
        }
    }
}
