using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClaudeManager.Hub.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddJiraIssueLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "JiraIssueLinks",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    IssueKey = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    IssueSummary = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    LinkType = table.Column<int>(type: "INTEGER", nullable: false),
                    SweAfJobId = table.Column<long>(type: "INTEGER", nullable: true),
                    SessionId = table.Column<string>(type: "TEXT", nullable: true),
                    LinkedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ReviewTransitionedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JiraIssueLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JiraIssueLinks_ClaudeSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "ClaudeSessions",
                        principalColumn: "SessionId");
                    table.ForeignKey(
                        name: "FK_JiraIssueLinks_SweAfJobs_SweAfJobId",
                        column: x => x.SweAfJobId,
                        principalTable: "SweAfJobs",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_JiraIssueLinks_IssueKey",
                table: "JiraIssueLinks",
                column: "IssueKey");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JiraIssueLinks");
        }
    }
}
