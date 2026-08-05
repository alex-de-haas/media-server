using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaServer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPlaybackPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlaybackPreferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AppUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    MediaItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AudioLanguage = table.Column<string>(type: "TEXT", nullable: true),
                    SubtitleLanguage = table.Column<string>(type: "TEXT", nullable: true),
                    SubtitlesForcedOnly = table.Column<bool>(type: "INTEGER", nullable: false),
                    PreferOriginalAudio = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlaybackPreferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlaybackPreferences_AppUsers_AppUserId",
                        column: x => x.AppUserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PlaybackPreferences_MediaItems_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlaybackPreferences_AppUserId_MediaItemId",
                table: "PlaybackPreferences",
                columns: new[] { "AppUserId", "MediaItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlaybackPreferences_MediaItemId",
                table: "PlaybackPreferences",
                column: "MediaItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlaybackPreferences");
        }
    }
}
