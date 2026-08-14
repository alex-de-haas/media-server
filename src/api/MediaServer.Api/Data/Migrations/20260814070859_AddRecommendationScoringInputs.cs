using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaServer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRecommendationScoringInputs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TmdbRecommendationCache_Kind_TmdbId",
                table: "TmdbRecommendationCache");

            migrationBuilder.AddColumn<int>(
                name: "Generator",
                table: "TmdbRecommendationCache",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PayloadVersion",
                table: "TmdbRecommendationCache",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "PopularityBias",
                table: "RecommendationPreferences",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.CreateIndex(
                name: "IX_TmdbRecommendationCache_Generator_Kind_TmdbId",
                table: "TmdbRecommendationCache",
                columns: new[] { "Generator", "Kind", "TmdbId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TmdbRecommendationCache_Generator_Kind_TmdbId",
                table: "TmdbRecommendationCache");

            migrationBuilder.DropColumn(
                name: "Generator",
                table: "TmdbRecommendationCache");

            migrationBuilder.DropColumn(
                name: "PayloadVersion",
                table: "TmdbRecommendationCache");

            migrationBuilder.DropColumn(
                name: "PopularityBias",
                table: "RecommendationPreferences");

            migrationBuilder.CreateIndex(
                name: "IX_TmdbRecommendationCache_Kind_TmdbId",
                table: "TmdbRecommendationCache",
                columns: new[] { "Kind", "TmdbId" },
                unique: true);
        }
    }
}
