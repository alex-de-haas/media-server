using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaServer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRetainedDownloadPlacement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OriginalRelativePath",
                table: "SourceFiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlacementHash",
                table: "SourceFiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlacementPath",
                table: "SourceFiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CancellationRequested",
                table: "Downloads",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "CleanupAfter",
                table: "Downloads",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CleanupAttempts",
                table: "Downloads",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "CleanupRequested",
                table: "Downloads",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "EngineReleased",
                table: "Downloads",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "PlacementBytes",
                table: "Downloads",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<bool>(
                name: "PlacementStarted",
                table: "Downloads",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "PlacementTotalBytes",
                table: "Downloads",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "RetainedBytes",
                table: "Downloads",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "RetentionError",
                table: "Downloads",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "StopRequested",
                table: "Downloads",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OriginalRelativePath",
                table: "SourceFiles");

            migrationBuilder.DropColumn(
                name: "PlacementHash",
                table: "SourceFiles");

            migrationBuilder.DropColumn(
                name: "PlacementPath",
                table: "SourceFiles");

            migrationBuilder.DropColumn(
                name: "CancellationRequested",
                table: "Downloads");

            migrationBuilder.DropColumn(
                name: "CleanupAfter",
                table: "Downloads");

            migrationBuilder.DropColumn(
                name: "CleanupAttempts",
                table: "Downloads");

            migrationBuilder.DropColumn(
                name: "CleanupRequested",
                table: "Downloads");

            migrationBuilder.DropColumn(
                name: "EngineReleased",
                table: "Downloads");

            migrationBuilder.DropColumn(
                name: "PlacementBytes",
                table: "Downloads");

            migrationBuilder.DropColumn(
                name: "PlacementStarted",
                table: "Downloads");

            migrationBuilder.DropColumn(
                name: "PlacementTotalBytes",
                table: "Downloads");

            migrationBuilder.DropColumn(
                name: "RetainedBytes",
                table: "Downloads");

            migrationBuilder.DropColumn(
                name: "RetentionError",
                table: "Downloads");

            migrationBuilder.DropColumn(
                name: "StopRequested",
                table: "Downloads");
        }
    }
}
