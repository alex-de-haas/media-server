using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaServer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameOutboxRemoteIdSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "PreCreateRemoteIds",
                table: "WatchHistoryOutboxEvents",
                newName: "RemoteIdSnapshot");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "RemoteIdSnapshot",
                table: "WatchHistoryOutboxEvents",
                newName: "PreCreateRemoteIds");
        }
    }
}
