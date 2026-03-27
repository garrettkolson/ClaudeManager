using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClaudeManager.Hub.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLlmDeploymentImageTag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name:         "ImageTag",
                table:        "LlmDeployments",
                type:         "TEXT",
                maxLength:    50,
                nullable:     false,
                defaultValue: "latest");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name:  "ImageTag",
                table: "LlmDeployments");
        }
    }
}
