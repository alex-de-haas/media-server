using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaServer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFavoritesSyncState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FavoritesCapacity",
                table: "WatchHistoryConnections",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FavoritesRemoteCount",
                table: "WatchHistoryConnections",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WatchHistoryFavoriteStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    IdentityProvider = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    IdentityProviderId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    RemotePresent = table.Column<bool>(type: "INTEGER", nullable: false),
                    RemoteFavoritedAt = table.Column<string>(type: "TEXT", nullable: true),
                    LocalFavorite = table.Column<bool>(type: "INTEGER", nullable: false),
                    ReconciledAt = table.Column<string>(type: "TEXT", nullable: false)
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

            migrationBuilder.CreateIndex(
                name: "IX_WatchHistoryFavoriteStates_ConnectionId_Kind_IdentityProvider_IdentityProviderId",
                table: "WatchHistoryFavoriteStates",
                columns: new[] { "ConnectionId", "Kind", "IdentityProvider", "IdentityProviderId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WatchHistoryFavoriteStates");

            migrationBuilder.DropColumn(
                name: "FavoritesCapacity",
                table: "WatchHistoryConnections");

            migrationBuilder.DropColumn(
                name: "FavoritesRemoteCount",
                table: "WatchHistoryConnections");
        }
    }
}
