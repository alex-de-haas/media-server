using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaServer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddChangeLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChangeLog",
                columns: table => new
                {
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EntityType = table.Column<int>(type: "INTEGER", nullable: false),
                    EntityId = table.Column<string>(type: "TEXT", nullable: false),
                    AppUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    OccurredAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChangeLog", x => x.Sequence);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChangeLog_OccurredAt",
                table: "ChangeLog",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_ChangeLog_Sequence_AppUserId",
                table: "ChangeLog",
                columns: new[] { "Sequence", "AppUserId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChangeLog");
        }
    }
}
