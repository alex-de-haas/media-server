using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaServer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddIngestTargetIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TargetKind",
                table: "IngestItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetProvider",
                table: "IngestItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetProviderId",
                table: "IngestItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetTitle",
                table: "IngestItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TargetYear",
                table: "IngestItems",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TargetKind",
                table: "IngestItems");

            migrationBuilder.DropColumn(
                name: "TargetProvider",
                table: "IngestItems");

            migrationBuilder.DropColumn(
                name: "TargetProviderId",
                table: "IngestItems");

            migrationBuilder.DropColumn(
                name: "TargetTitle",
                table: "IngestItems");

            migrationBuilder.DropColumn(
                name: "TargetYear",
                table: "IngestItems");
        }
    }
}
