using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaServer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AnchorCatalogsToMounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Catalogs_Root",
                table: "Catalogs");

            migrationBuilder.AddColumn<string>(
                name: "MountLabel",
                table: "Catalogs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MountRelativePath",
                table: "Catalogs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Catalogs_MountLabel_MountRelativePath",
                table: "Catalogs",
                columns: new[] { "MountLabel", "MountRelativePath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Catalogs_Root",
                table: "Catalogs",
                column: "Root",
                unique: true,
                filter: "\"MountLabel\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Catalogs_MountLabel_MountRelativePath",
                table: "Catalogs");

            migrationBuilder.DropIndex(
                name: "IX_Catalogs_Root",
                table: "Catalogs");

            migrationBuilder.DropColumn(
                name: "MountLabel",
                table: "Catalogs");

            migrationBuilder.DropColumn(
                name: "MountRelativePath",
                table: "Catalogs");

            migrationBuilder.CreateIndex(
                name: "IX_Catalogs_Root",
                table: "Catalogs",
                column: "Root",
                unique: true);
        }
    }
}
