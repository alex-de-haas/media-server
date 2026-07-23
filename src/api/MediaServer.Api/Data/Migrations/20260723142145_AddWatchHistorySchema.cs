using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaServer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWatchHistorySchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastWatchedAt",
                table: "UserItemData",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StateRevision",
                table: "UserItemData",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "WatchedStateChangedAt",
                table: "UserItemData",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "HistoryEntryId",
                table: "PlaybackSessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PlaybackHistoryEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AppUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    MediaItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    WatchedAt = table.Column<string>(type: "TEXT", nullable: true),
                    Origin = table.Column<int>(type: "INTEGER", nullable: false),
                    PlaySessionId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    IdentitySnapshot = table.Column<string>(type: "TEXT", nullable: true),
                    ProviderKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ProviderHistoryId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ProviderEntryOwned = table.Column<bool>(type: "INTEGER", nullable: false),
                    LinkStatus = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlaybackHistoryEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlaybackHistoryEntries_AppUsers_AppUserId",
                        column: x => x.AppUserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PlaybackHistoryEntries_MediaItems_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WatchHistoryAuthorizations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AppUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProviderKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    UserCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    VerificationUrl = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<string>(type: "TEXT", nullable: false),
                    PollIntervalSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    NextPollAt = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false)
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
                    ProviderKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ProviderAccountId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ProviderAccountName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    SecretKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CredentialExpiresAt = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ConnectedAt = table.Column<string>(type: "TEXT", nullable: false),
                    LastDeliveryAt = table.Column<string>(type: "TEXT", nullable: true),
                    LastSyncAt = table.Column<string>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true)
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
                name: "WatchHistoryOutboxEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AppUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    MediaItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    HistoryEntryId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Operation = table.Column<int>(type: "INTEGER", nullable: false),
                    IdentitySnapshot = table.Column<string>(type: "TEXT", nullable: true),
                    OccurredAt = table.Column<string>(type: "TEXT", nullable: true),
                    PreCreateRemoteIds = table.Column<string>(type: "TEXT", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    LeaseUntil = table.Column<string>(type: "TEXT", nullable: true),
                    NextAttemptAt = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<string>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true)
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
                    Scope = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Counts = table.Column<string>(type: "TEXT", nullable: true),
                    Issues = table.Column<string>(type: "TEXT", nullable: true),
                    CapturedRevisions = table.Column<string>(type: "TEXT", nullable: true),
                    HasPendingOutboundWork = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<string>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<string>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<string>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true)
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
                name: "IX_PlaybackHistoryEntries_AppUserId_MediaItemId_PlaySessionId",
                table: "PlaybackHistoryEntries",
                columns: new[] { "AppUserId", "MediaItemId", "PlaySessionId" },
                unique: true,
                filter: "\"PlaySessionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PlaybackHistoryEntries_AppUserId_MediaItemId_WatchedAt",
                table: "PlaybackHistoryEntries",
                columns: new[] { "AppUserId", "MediaItemId", "WatchedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PlaybackHistoryEntries_MediaItemId",
                table: "PlaybackHistoryEntries",
                column: "MediaItemId");

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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlaybackHistoryEntries");

            migrationBuilder.DropTable(
                name: "WatchHistoryAuthorizations");

            migrationBuilder.DropTable(
                name: "WatchHistoryOutboxEvents");

            migrationBuilder.DropTable(
                name: "WatchHistorySyncRuns");

            migrationBuilder.DropTable(
                name: "WatchHistoryConnections");

            migrationBuilder.DropColumn(
                name: "LastWatchedAt",
                table: "UserItemData");

            migrationBuilder.DropColumn(
                name: "StateRevision",
                table: "UserItemData");

            migrationBuilder.DropColumn(
                name: "WatchedStateChangedAt",
                table: "UserItemData");

            migrationBuilder.DropColumn(
                name: "HistoryEntryId",
                table: "PlaybackSessions");
        }
    }
}
