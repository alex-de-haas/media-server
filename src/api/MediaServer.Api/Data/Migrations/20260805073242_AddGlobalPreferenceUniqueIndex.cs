using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaServer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGlobalPreferenceUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_PlaybackPreferences_AppUserId_Global",
                table: "PlaybackPreferences",
                column: "AppUserId",
                unique: true,
                filter: "\"MediaItemId\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PlaybackPreferences_AppUserId_Global",
                table: "PlaybackPreferences");
        }
    }
}
