using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClaudeManager.Hub.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPerBuildProvisioning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SweAfJobs — per-build container tracking
            migrationBuilder.AddColumn<int>(
                name: "AllocatedPort",
                table: "SweAfJobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ControlPlaneUrl",
                table: "SweAfJobs",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ComposeProjectName",
                table: "SweAfJobs",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            // SweAfConfigs — per-build provisioning settings
            migrationBuilder.AddColumn<int>(
                name: "PortRangeStart",
                table: "SweAfConfigs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 8100);

            migrationBuilder.AddColumn<int>(
                name: "PortRangeEnd",
                table: "SweAfConfigs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 8199);

            migrationBuilder.AddColumn<string>(
                name: "ControlPlaneImageTag",
                table: "SweAfConfigs",
                type: "TEXT",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "AllocatedPort",         table: "SweAfJobs");
            migrationBuilder.DropColumn(name: "ControlPlaneUrl",       table: "SweAfJobs");
            migrationBuilder.DropColumn(name: "ComposeProjectName",    table: "SweAfJobs");
            migrationBuilder.DropColumn(name: "PortRangeStart",        table: "SweAfConfigs");
            migrationBuilder.DropColumn(name: "PortRangeEnd",          table: "SweAfConfigs");
            migrationBuilder.DropColumn(name: "ControlPlaneImageTag",  table: "SweAfConfigs");
        }
    }
}
