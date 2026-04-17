using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClaudeManager.Hub.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PendingChangesDiag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_JiraIssueLinks_ClaudeSessions_SessionId",
                table: "JiraIssueLinks");

            migrationBuilder.DropForeignKey(
                name: "FK_JiraIssueLinks_SweAfJobs_SweAfJobId",
                table: "JiraIssueLinks");

            migrationBuilder.AddColumn<string>(
                name: "ClaudeSessionSessionId",
                table: "JiraIssueLinks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_JiraIssueLinks_ClaudeSessionSessionId",
                table: "JiraIssueLinks",
                column: "ClaudeSessionSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_JiraIssueLinks_SweAfJobId",
                table: "JiraIssueLinks",
                column: "SweAfJobId");

            migrationBuilder.AddForeignKey(
                name: "FK_JiraIssueLinks_ClaudeSessions_ClaudeSessionSessionId",
                table: "JiraIssueLinks",
                column: "ClaudeSessionSessionId",
                principalTable: "ClaudeSessions",
                principalColumn: "SessionId");

            migrationBuilder.AddForeignKey(
                name: "FK_JiraIssueLinks_SweAfJobs_SweAfJobId",
                table: "JiraIssueLinks",
                column: "SweAfJobId",
                principalTable: "SweAfJobs",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_JiraIssueLinks_ClaudeSessions_ClaudeSessionSessionId",
                table: "JiraIssueLinks");

            migrationBuilder.DropForeignKey(
                name: "FK_JiraIssueLinks_SweAfJobs_SweAfJobId",
                table: "JiraIssueLinks");

            migrationBuilder.DropIndex(
                name: "IX_JiraIssueLinks_ClaudeSessionSessionId",
                table: "JiraIssueLinks");

            migrationBuilder.DropIndex(
                name: "IX_JiraIssueLinks_SweAfJobId",
                table: "JiraIssueLinks");

            migrationBuilder.DropColumn(
                name: "ClaudeSessionSessionId",
                table: "JiraIssueLinks");

            migrationBuilder.AddForeignKey(
                name: "FK_JiraIssueLinks_ClaudeSessions_SessionId",
                table: "JiraIssueLinks",
                column: "SessionId",
                principalTable: "ClaudeSessions",
                principalColumn: "SessionId",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_JiraIssueLinks_SweAfJobs_SweAfJobId",
                table: "JiraIssueLinks",
                column: "SweAfJobId",
                principalTable: "SweAfJobs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
