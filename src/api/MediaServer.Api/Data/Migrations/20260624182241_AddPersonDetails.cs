using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaServer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPersonDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Birthday",
                table: "Persons",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Deathday",
                table: "Persons",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DetailsFetchedAt",
                table: "Persons",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlaceOfBirth",
                table: "Persons",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Birthday",
                table: "Persons");

            migrationBuilder.DropColumn(
                name: "Deathday",
                table: "Persons");

            migrationBuilder.DropColumn(
                name: "DetailsFetchedAt",
                table: "Persons");

            migrationBuilder.DropColumn(
                name: "PlaceOfBirth",
                table: "Persons");
        }
    }
}
