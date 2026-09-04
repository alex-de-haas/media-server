using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaServer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDolbyVisionDetail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DolbyVision",
                table: "TranscodeJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DvBlSignalCompatibilityId",
                table: "MediaStreams",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "DvElPresent",
                table: "MediaStreams",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DvLevel",
                table: "MediaStreams",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DvProfile",
                table: "MediaStreams",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DolbyVision",
                table: "TranscodeJobs");

            migrationBuilder.DropColumn(
                name: "DvBlSignalCompatibilityId",
                table: "MediaStreams");

            migrationBuilder.DropColumn(
                name: "DvElPresent",
                table: "MediaStreams");

            migrationBuilder.DropColumn(
                name: "DvLevel",
                table: "MediaStreams");

            migrationBuilder.DropColumn(
                name: "DvProfile",
                table: "MediaStreams");
        }
    }
}
