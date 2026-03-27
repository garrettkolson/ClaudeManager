using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClaudeManager.Hub.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLlmDeployments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LlmDeployments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DeploymentId    = table.Column<string>(type: "TEXT", maxLength: 100,  nullable: false),
                    HostId          = table.Column<string>(type: "TEXT", maxLength: 100,  nullable: false),
                    ModelId         = table.Column<string>(type: "TEXT", maxLength: 500,  nullable: false),
                    GpuIndices      = table.Column<string>(type: "TEXT", maxLength: 200,  nullable: false),
                    HostPort        = table.Column<int>(type: "INTEGER", nullable: false),
                    Quantization    = table.Column<string>(type: "TEXT", maxLength: 50,   nullable: false),
                    ExtraArgs       = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    HfTokenOverride = table.Column<string>(type: "TEXT", maxLength: 500,  nullable: true),
                    Status          = table.Column<int>(type: "INTEGER", nullable: false),
                    ContainerId     = table.Column<string>(type: "TEXT", maxLength: 100,  nullable: true),
                    ErrorMessage    = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CreatedAt       = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    StartedAt       = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LlmDeployments", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LlmDeployments_HostId",
                table: "LlmDeployments",
                column: "HostId");

            migrationBuilder.CreateIndex(
                name: "IX_LlmDeployments_Status",
                table: "LlmDeployments",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "LlmDeployments");
        }
    }
}
