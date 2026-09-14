using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaServer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class LocalWatchHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WatchHistoryAuthorizations");

            migrationBuilder.DropTable(
                name: "WatchHistoryFavoriteStates");

            migrationBuilder.DropTable(
                name: "WatchHistoryOutboxEvents");

            migrationBuilder.DropTable(
                name: "WatchHistorySyncRuns");

            migrationBuilder.DropTable(
                name: "WatchHistoryConnections");

            migrationBuilder.DropIndex(
                name: "IX_PlaybackHistoryEntries_ProviderKey_ProviderHistoryId",
                table: "PlaybackHistoryEntries");

            migrationBuilder.DropColumn(
                name: "IdentitySnapshot",
                table: "PlaybackHistoryEntries");

            migrationBuilder.DropColumn(
                name: "LinkStatus",
                table: "PlaybackHistoryEntries");

            migrationBuilder.DropColumn(
                name: "ProviderEntryOwned",
                table: "PlaybackHistoryEntries");

            migrationBuilder.DropColumn(
                name: "ProviderHistoryId",
                table: "PlaybackHistoryEntries");

            migrationBuilder.DropColumn(
                name: "ProviderKey",
                table: "PlaybackHistoryEntries");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IdentitySnapshot",
                table: "PlaybackHistoryEntries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LinkStatus",
                table: "PlaybackHistoryEntries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "ProviderEntryOwned",
                table: "PlaybackHistoryEntries",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ProviderHistoryId",
                table: "PlaybackHistoryEntries",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderKey",
                table: "PlaybackHistoryEntries",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WatchHistoryAuthorizations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AppUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<string>(type: "TEXT", nullable: false),
                    NextPollAt = table.Column<string>(type: "TEXT", nullable: false),
                    PollIntervalSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    ProviderKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    UserCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    VerificationUrl = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WatchHistoryAuthorizations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WatchHistoryAuthorizations_AppUsers_AppUserId",
                        column: x => x.AppUserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WatchHistoryConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AppUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    ConnectedAt = table.Column<string>(type: "TEXT", nullable: false),
                    CredentialExpiresAt = table.Column<string>(type: "TEXT", nullable: true),
                    FavoritesCapacity = table.Column<int>(type: "INTEGER", nullable: true),
                    FavoritesRemoteCount = table.Column<int>(type: "INTEGER", nullable: true),
                    LastDeliveryAt = table.Column<string>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    LastSyncAt = table.Column<string>(type: "TEXT", nullable: true),
                    ProviderAccountId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ProviderAccountName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ProviderKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SecretKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WatchHistoryConnections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WatchHistoryConnections_AppUsers_AppUserId",
                        column: x => x.AppUserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WatchHistoryFavoriteStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IdentityProvider = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    IdentityProviderId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    LocalFavorite = table.Column<bool>(type: "INTEGER", nullable: false),
                    ReconciledAt = table.Column<string>(type: "TEXT", nullable: false),
                    RemoteFavoritedAt = table.Column<string>(type: "TEXT", nullable: true),
                    RemotePresent = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WatchHistoryFavoriteStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WatchHistoryFavoriteStates_WatchHistoryConnections_ConnectionId",
                        column: x => x.ConnectionId,
                        principalTable: "WatchHistoryConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WatchHistoryOutboxEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AppUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    CompletedAt = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    HistoryEntryId = table.Column<Guid>(type: "TEXT", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    IdentitySnapshot = table.Column<string>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    LeaseUntil = table.Column<string>(type: "TEXT", nullable: true),
                    MediaItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    NextAttemptAt = table.Column<string>(type: "TEXT", nullable: true),
                    OccurredAt = table.Column<string>(type: "TEXT", nullable: true),
                    Operation = table.Column<int>(type: "INTEGER", nullable: false),
                    RemoteIdSnapshot = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WatchHistoryOutboxEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WatchHistoryOutboxEvents_WatchHistoryConnections_ConnectionId",
                        column: x => x.ConnectionId,
                        principalTable: "WatchHistoryConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WatchHistorySyncRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AppUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CapturedRevisions = table.Column<string>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<string>(type: "TEXT", nullable: true),
                    Counts = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<string>(type: "TEXT", nullable: false),
                    HasPendingOutboundWork = table.Column<bool>(type: "INTEGER", nullable: false),
                    Issues = table.Column<string>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    Scope = table.Column<string>(type: "TEXT", nullable: true),
                    StartedAt = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WatchHistorySyncRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WatchHistorySyncRuns_AppUsers_AppUserId",
                        column: x => x.AppUserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WatchHistorySyncRuns_WatchHistoryConnections_ConnectionId",
                        column: x => x.ConnectionId,
                        principalTable: "WatchHistoryConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlaybackHistoryEntries_ProviderKey_ProviderHistoryId",
                table: "PlaybackHistoryEntries",
                columns: new[] { "ProviderKey", "ProviderHistoryId" });

            migrationBuilder.CreateIndex(
                name: "IX_WatchHistoryAuthorizations_AppUserId_ProviderKey",
                table: "WatchHistoryAuthorizations",
                columns: new[] { "AppUserId", "ProviderKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WatchHistoryConnections_AppUserId_ProviderKey",
                table: "WatchHistoryConnections",
                columns: new[] { "AppUserId", "ProviderKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WatchHistoryFavoriteStates_ConnectionId_Kind_IdentityProvider_IdentityProviderId",
                table: "WatchHistoryFavoriteStates",
                columns: new[] { "ConnectionId", "Kind", "IdentityProvider", "IdentityProviderId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WatchHistoryOutboxEvents_ConnectionId",
                table: "WatchHistoryOutboxEvents",
                column: "ConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_WatchHistoryOutboxEvents_IdempotencyKey",
                table: "WatchHistoryOutboxEvents",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WatchHistoryOutboxEvents_Status_NextAttemptAt",
                table: "WatchHistoryOutboxEvents",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WatchHistorySyncRuns_AppUserId_CreatedAt",
                table: "WatchHistorySyncRuns",
                columns: new[] { "AppUserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WatchHistorySyncRuns_ConnectionId",
                table: "WatchHistorySyncRuns",
                column: "ConnectionId");
        }
    }
}
