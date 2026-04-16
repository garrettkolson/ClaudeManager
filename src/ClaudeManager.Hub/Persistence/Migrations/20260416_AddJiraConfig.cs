using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClaudeManager.Hub.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddJiraConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "JiraConfigs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BaseUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Email = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    ApiToken = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    DefaultProjectKey = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    DefaultJql = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    DefaultRepoUrl = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    OnDeckStatusName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ReviewStatusName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    PollingIntervalSecs = table.Column<int>(type: "INTEGER", nullable: false),
                    WebhookSecret = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JiraConfigs", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "JiraConfigs");
        }
    }
}
