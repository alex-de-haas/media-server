using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaServer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBluraySources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BluraySelectionJson",
                table: "TranscodeJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DiscFileIndexesJson",
                table: "SourceFiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "SourceFiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "PendingLibraryPath",
                table: "SourceFiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "MediaSources",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BluraySelectionJson",
                table: "TranscodeJobs");

            migrationBuilder.DropColumn(
                name: "DiscFileIndexesJson",
                table: "SourceFiles");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "SourceFiles");

            migrationBuilder.DropColumn(
                name: "PendingLibraryPath",
                table: "SourceFiles");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "MediaSources");
        }
    }
}
