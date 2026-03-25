using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClaudeManager.Hub.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MachineAgents",
                columns: table => new
                {
                    MachineId = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    Platform = table.Column<string>(type: "TEXT", nullable: false),
                    FirstConnectedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastConnectedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MachineAgents", x => x.MachineId);
                });

            migrationBuilder.CreateTable(
                name: "ClaudeSessions",
                columns: table => new
                {
                    SessionId = table.Column<string>(type: "TEXT", nullable: false),
                    MachineId = table.Column<string>(type: "TEXT", nullable: false),
                    WorkingDirectory = table.Column<string>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    InitialPrompt = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    LastActivityAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClaudeSessions", x => x.SessionId);
                    table.ForeignKey(
                        name: "FK_ClaudeSessions_MachineAgents_MachineId",
                        column: x => x.MachineId,
                        principalTable: "MachineAgents",
                        principalColumn: "MachineId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StreamedLines",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SessionId = table.Column<string>(type: "TEXT", nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Content = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: false),
                    IsContentTruncated = table.Column<bool>(type: "INTEGER", nullable: false),
                    ToolName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StreamedLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StreamedLines_ClaudeSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "ClaudeSessions",
                        principalColumn: "SessionId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClaudeSessions_LastActivityAt",
                table: "ClaudeSessions",
                column: "LastActivityAt");

            migrationBuilder.CreateIndex(
                name: "IX_ClaudeSessions_MachineId",
                table: "ClaudeSessions",
                column: "MachineId");

            migrationBuilder.CreateIndex(
                name: "IX_ClaudeSessions_Status",
                table: "ClaudeSessions",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_StreamedLines_SessionId",
                table: "StreamedLines",
                column: "SessionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StreamedLines");

            migrationBuilder.DropTable(
                name: "ClaudeSessions");

            migrationBuilder.DropTable(
                name: "MachineAgents");
        }
    }
}
