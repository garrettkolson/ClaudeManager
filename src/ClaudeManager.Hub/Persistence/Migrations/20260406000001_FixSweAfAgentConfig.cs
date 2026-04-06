using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClaudeManager.Hub.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FixSweAfAgentConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GhToken",
                table: "SweAfConfigs");

            migrationBuilder.DropColumn(
                name: "SweAfAgentImage",
                table: "SweAfConfigs");

            migrationBuilder.AddColumn<string>(
                name: "SweAfRepoPath",
                table: "SweAfConfigs",
                type: "TEXT",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SweAfRepoPath",
                table: "SweAfConfigs");

            migrationBuilder.AddColumn<string>(
                name: "GhToken",
                table: "SweAfConfigs",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SweAfAgentImage",
                table: "SweAfConfigs",
                type: "TEXT",
                maxLength: 500,
                nullable: true);
        }
    }
}
