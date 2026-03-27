using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClaudeManager.Hub.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLlmHosting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GpuHosts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    HostId      = table.Column<string>(type: "TEXT", maxLength: 100,  nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200,  nullable: false),
                    Host        = table.Column<string>(type: "TEXT", maxLength: 500,  nullable: false),
                    SshPort     = table.Column<int>(type: "INTEGER", nullable: false),
                    SshUser     = table.Column<string>(type: "TEXT", maxLength: 100,  nullable: true),
                    SshKeyPath  = table.Column<string>(type: "TEXT", maxLength: 500,  nullable: true),
                    SshPassword = table.Column<string>(type: "TEXT", maxLength: 500,  nullable: true),
                    AddedAt     = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GpuHosts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HubSecrets",
                columns: table => new
                {
                    Key   = table.Column<string>(type: "TEXT", maxLength: 100,  nullable: false),
                    Value = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HubSecrets", x => x.Key);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GpuHosts_HostId",
                table: "GpuHosts",
                column: "HostId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "GpuHosts");
            migrationBuilder.DropTable(name: "HubSecrets");
        }
    }
}
