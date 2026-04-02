using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClaudeManager.Hub.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSweAfConfigSudo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProvisionHost",
                table: "SweAfConfigs",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequiresSudo",
                table: "SweAfConfigs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SshKeyPath",
                table: "SweAfConfigs",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SshPassword",
                table: "SweAfConfigs",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SshPort",
                table: "SweAfConfigs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SshUser",
                table: "SweAfConfigs",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SudoPassword",
                table: "SweAfConfigs",
                type: "TEXT",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProvisionHost",
                table: "SweAfConfigs");

            migrationBuilder.DropColumn(
                name: "RequiresSudo",
                table: "SweAfConfigs");

            migrationBuilder.DropColumn(
                name: "SshKeyPath",
                table: "SweAfConfigs");

            migrationBuilder.DropColumn(
                name: "SshPassword",
                table: "SweAfConfigs");

            migrationBuilder.DropColumn(
                name: "SshPort",
                table: "SweAfConfigs");

            migrationBuilder.DropColumn(
                name: "SshUser",
                table: "SweAfConfigs");

            migrationBuilder.DropColumn(
                name: "SudoPassword",
                table: "SweAfConfigs");
        }
    }
}
