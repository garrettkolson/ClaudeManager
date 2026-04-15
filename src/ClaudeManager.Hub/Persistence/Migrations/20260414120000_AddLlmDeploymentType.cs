using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClaudeManager.Hub.Persistence.Migrations;

/// <inheritdoc />
public partial class AddLlmDeploymentType : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "DeploymentType",
            table: "LlmDeployments",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<int>(
            name: "NggpuLayers",
            table: "LlmDeployments",
            type: "INTEGER",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "DeploymentType",
            table: "LlmDeployments");

        migrationBuilder.DropColumn(
            name: "NggpuLayers",
            table: "LlmDeployments");
    }
}
