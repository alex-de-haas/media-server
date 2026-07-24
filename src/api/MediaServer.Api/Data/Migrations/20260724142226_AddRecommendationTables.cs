using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaServer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRecommendationTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RecommendationHides",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AppUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    TmdbId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecommendationHides", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecommendationHides_AppUsers_AppUserId",
                        column: x => x.AppUserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RecommendationPreferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AppUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Sources = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecommendationPreferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecommendationPreferences_AppUsers_AppUserId",
                        column: x => x.AppUserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TmdbRecommendationCache",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    TmdbId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Payload = table.Column<string>(type: "TEXT", nullable: false),
                    FetchedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TmdbRecommendationCache", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RecommendationHides_AppUserId_Kind_TmdbId",
                table: "RecommendationHides",
                columns: new[] { "AppUserId", "Kind", "TmdbId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecommendationPreferences_AppUserId",
                table: "RecommendationPreferences",
                column: "AppUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TmdbRecommendationCache_Kind_TmdbId",
                table: "TmdbRecommendationCache",
                columns: new[] { "Kind", "TmdbId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RecommendationHides");

            migrationBuilder.DropTable(
                name: "RecommendationPreferences");

            migrationBuilder.DropTable(
                name: "TmdbRecommendationCache");
        }
    }
}
