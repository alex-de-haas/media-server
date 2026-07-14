using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaServer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddReleaseTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TrackedTitles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    IdentityProvider = table.Column<string>(type: "TEXT", nullable: false),
                    IdentityProviderId = table.Column<string>(type: "TEXT", nullable: false),
                    Providers = table.Column<string>(type: "TEXT", nullable: false),
                    MediaItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Year = table.Column<int>(type: "INTEGER", nullable: true),
                    PosterUrl = table.Column<string>(type: "TEXT", nullable: true),
                    ProductionStatus = table.Column<string>(type: "TEXT", nullable: true),
                    LastAiredSeason = table.Column<int>(type: "INTEGER", nullable: true),
                    LastAiredEpisode = table.Column<int>(type: "INTEGER", nullable: true),
                    LastAiredDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    LastRefreshedAt = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackedTitles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TrackedTitles_MediaItems_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ReleaseReminders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AppUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    TrackedTitleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReleaseType = table.Column<int>(type: "INTEGER", nullable: false),
                    LeadDays = table.Column<int>(type: "INTEGER", nullable: false),
                    NotifyAt = table.Column<TimeOnly>(type: "TEXT", nullable: false),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReleaseReminders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReleaseReminders_AppUsers_AppUserId",
                        column: x => x.AppUserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ReleaseReminders_TrackedTitles_TrackedTitleId",
                        column: x => x.TrackedTitleId,
                        principalTable: "TrackedTitles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TrackedReleases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TrackedTitleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Region = table.Column<string>(type: "TEXT", nullable: true),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    RawType = table.Column<int>(type: "INTEGER", nullable: true),
                    Season = table.Column<int>(type: "INTEGER", nullable: true),
                    Episode = table.Column<int>(type: "INTEGER", nullable: true),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    PreviousDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackedReleases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TrackedReleases_TrackedTitles_TrackedTitleId",
                        column: x => x.TrackedTitleId,
                        principalTable: "TrackedTitles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WatchlistEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AppUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    TrackedTitleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MonitorScope = table.Column<int>(type: "INTEGER", nullable: true),
                    MonitoredSeasons = table.Column<string>(type: "TEXT", nullable: true),
                    RegionOverride = table.Column<string>(type: "TEXT", nullable: true),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WatchlistEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WatchlistEntries_AppUsers_AppUserId",
                        column: x => x.AppUserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WatchlistEntries_TrackedTitles_TrackedTitleId",
                        column: x => x.TrackedTitleId,
                        principalTable: "TrackedTitles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReminderDeliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReminderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TrackedReleaseId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SentAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReminderDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReminderDeliveries_ReleaseReminders_ReminderId",
                        column: x => x.ReminderId,
                        principalTable: "ReleaseReminders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ReminderDeliveries_TrackedReleases_TrackedReleaseId",
                        column: x => x.TrackedReleaseId,
                        principalTable: "TrackedReleases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReleaseReminders_AppUserId_TrackedTitleId_ReleaseType",
                table: "ReleaseReminders",
                columns: new[] { "AppUserId", "TrackedTitleId", "ReleaseType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReleaseReminders_TrackedTitleId",
                table: "ReleaseReminders",
                column: "TrackedTitleId");

            migrationBuilder.CreateIndex(
                name: "IX_ReminderDeliveries_ReminderId_TrackedReleaseId",
                table: "ReminderDeliveries",
                columns: new[] { "ReminderId", "TrackedReleaseId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReminderDeliveries_TrackedReleaseId",
                table: "ReminderDeliveries",
                column: "TrackedReleaseId");

            migrationBuilder.CreateIndex(
                name: "IX_TrackedReleases_Date",
                table: "TrackedReleases",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_TrackedReleases_EpisodeIdentity",
                table: "TrackedReleases",
                columns: new[] { "TrackedTitleId", "Type", "Season", "Episode" },
                unique: true,
                filter: "\"Region\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TrackedReleases_MovieIdentity",
                table: "TrackedReleases",
                columns: new[] { "TrackedTitleId", "Region", "Type" },
                unique: true,
                filter: "\"Region\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TrackedTitles_IdentityProvider_IdentityProviderId",
                table: "TrackedTitles",
                columns: new[] { "IdentityProvider", "IdentityProviderId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TrackedTitles_MediaItemId",
                table: "TrackedTitles",
                column: "MediaItemId");

            migrationBuilder.CreateIndex(
                name: "IX_WatchlistEntries_AppUserId_TrackedTitleId",
                table: "WatchlistEntries",
                columns: new[] { "AppUserId", "TrackedTitleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WatchlistEntries_TrackedTitleId",
                table: "WatchlistEntries",
                column: "TrackedTitleId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReminderDeliveries");

            migrationBuilder.DropTable(
                name: "WatchlistEntries");

            migrationBuilder.DropTable(
                name: "ReleaseReminders");

            migrationBuilder.DropTable(
                name: "TrackedReleases");

            migrationBuilder.DropTable(
                name: "TrackedTitles");
        }
    }
}
