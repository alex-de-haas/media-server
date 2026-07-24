using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaServer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWatchHistoryCalendarIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_PlaybackHistoryEntries_AppUserId_WatchedAt",
                table: "PlaybackHistoryEntries",
                columns: new[] { "AppUserId", "WatchedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PlaybackHistoryEntries_AppUserId_WatchedAt",
                table: "PlaybackHistoryEntries");
        }
    }
}
