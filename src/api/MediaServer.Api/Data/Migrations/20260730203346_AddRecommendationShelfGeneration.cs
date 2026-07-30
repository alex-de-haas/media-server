using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaServer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRecommendationShelfGeneration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GeneratedAt",
                table: "RecommendationShelfItems");

            migrationBuilder.CreateTable(
                name: "RecommendationShelfGenerations",
                columns: table => new
                {
                    AppUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    GeneratedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecommendationShelfGenerations", x => x.AppUserId);
                    table.ForeignKey(
                        name: "FK_RecommendationShelfGenerations_AppUsers_AppUserId",
                        column: x => x.AppUserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RecommendationShelfGenerations");

            migrationBuilder.AddColumn<string>(
                name: "GeneratedAt",
                table: "RecommendationShelfItems",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }
    }
}
