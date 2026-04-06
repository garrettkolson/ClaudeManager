using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClaudeManager.Hub.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSweAfAgentConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AnthropicApiKey",
                table: "SweAfConfigs",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

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
                name: "AnthropicApiKey",
                table: "SweAfConfigs");

            migrationBuilder.DropColumn(
                name: "SweAfRepoPath",
                table: "SweAfConfigs");
        }
    }
}
