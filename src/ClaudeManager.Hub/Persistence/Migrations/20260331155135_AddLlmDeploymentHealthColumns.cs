using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClaudeManager.Hub.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLlmDeploymentHealthColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RestartCount",
                table: "LlmDeployments",
                type: "INTEGER",
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastHealthCheckAt",
                table: "LlmDeployments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequiresSudo",
                table: "GpuHosts",
                type: "INTEGER",
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SudoPassword",
                table: "GpuHosts",
                type: "TEXT",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RestartCount",
                table: "LlmDeployments");

            migrationBuilder.DropColumn(
                name: "LastHealthCheckAt",
                table: "LlmDeployments");

            migrationBuilder.DropColumn(
                name: "RequiresSudo",
                table: "GpuHosts");

            migrationBuilder.DropColumn(
                name: "SudoPassword",
                table: "GpuHosts");
        }
    }
}
