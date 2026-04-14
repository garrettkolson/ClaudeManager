using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClaudeManager.Hub.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLlmDeploymentVllmOptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "UseHostNetwork",
                table: "LlmDeployments",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ShmSize",
                table: "LlmDeployments",
                type: "TEXT",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ServedModelName",
                table: "LlmDeployments",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "GpuMemoryUtilization",
                table: "LlmDeployments",
                type: "REAL",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UseHostNetwork",
                table: "LlmDeployments");

            migrationBuilder.DropColumn(
                name: "ShmSize",
                table: "LlmDeployments");

            migrationBuilder.DropColumn(
                name: "ServedModelName",
                table: "LlmDeployments");

            migrationBuilder.DropColumn(
                name: "GpuMemoryUtilization",
                table: "LlmDeployments");
        }
    }
}
