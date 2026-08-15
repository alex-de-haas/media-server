using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaServer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddArtworkLanguageSelection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ImageAssets_MediaItemId",
                table: "ImageAssets");

            migrationBuilder.AddColumn<string>(
                name: "PreferredPosterTag",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);

            // An existing database can already hold two rows for one (MediaItemId, RemotePath) — precisely the
            // enrich race the unique index below exists to prevent — and creating the index over them would
            // fail the migration. The earliest-inserted row survives; a duplicate carries the same remote path
            // and therefore the same Tag and the same cached binary, so no artwork a client has already been
            // handed is lost, and any cache file left behind is reclaimed by ImageCacheSweeper.
            migrationBuilder.Sql(
                """
                DELETE FROM "ImageAssets"
                WHERE rowid NOT IN (
                    SELECT MIN(rowid) FROM "ImageAssets" GROUP BY "MediaItemId", "RemotePath"
                );
                """);

            migrationBuilder.CreateIndex(
                name: "IX_ImageAssets_MediaItemId_RemotePath",
                table: "ImageAssets",
                columns: new[] { "MediaItemId", "RemotePath" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ImageAssets_MediaItemId_RemotePath",
                table: "ImageAssets");

            migrationBuilder.DropColumn(
                name: "PreferredPosterTag",
                table: "MediaItems");

            migrationBuilder.CreateIndex(
                name: "IX_ImageAssets_MediaItemId",
                table: "ImageAssets",
                column: "MediaItemId");
        }
    }
}
