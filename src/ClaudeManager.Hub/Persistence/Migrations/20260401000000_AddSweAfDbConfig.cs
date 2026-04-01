using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClaudeManager.Hub.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSweAfDbConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SweAfConfigs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BaseUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false, defaultValue: ""),
                    ApiKey = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    WebhookSecret = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    HubPublicUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Runtime = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, defaultValue: "claude_code"),
                    ModelDefault = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ModelCoder = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ModelQa = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    DefaultRepoUrl = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SweAfConfigs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SweAfHosts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Host = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    SshPort = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 22),
                    SshUser = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    SshKeyPath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    SshPassword = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    AnthropicBaseUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    AnthropicApiKey = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CommandsJson = table.Column<string>(type: "TEXT", nullable: true),
                    AddedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SweAfHosts", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "SweAfConfigs");
            migrationBuilder.DropTable(name: "SweAfHosts");
        }
    }
}
