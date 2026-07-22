using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaServer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPlaybackSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlaybackSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AppUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    MediaItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    StartedAt = table.Column<string>(type: "TEXT", nullable: false),
                    LastReportAt = table.Column<string>(type: "TEXT", nullable: false),
                    ObservedBelowThreshold = table.Column<bool>(type: "INTEGER", nullable: false),
                    CompletedAt = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlaybackSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlaybackSessions_AppUsers_AppUserId",
                        column: x => x.AppUserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PlaybackSessions_MediaItems_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlaybackSessions_AppUserId_MediaItemId_SessionKey",
                table: "PlaybackSessions",
                columns: new[] { "AppUserId", "MediaItemId", "SessionKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlaybackSessions_LastReportAt",
                table: "PlaybackSessions",
                column: "LastReportAt");

            migrationBuilder.CreateIndex(
                name: "IX_PlaybackSessions_MediaItemId",
                table: "PlaybackSessions",
                column: "MediaItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlaybackSessions");
        }
    }
}
