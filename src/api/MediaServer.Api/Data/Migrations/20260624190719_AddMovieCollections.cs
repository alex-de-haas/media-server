using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaServer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMovieCollections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CollectionId",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MovieCollections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", nullable: false),
                    ProviderId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    PosterPath = table.Column<string>(type: "TEXT", nullable: true),
                    PosterUrl = table.Column<string>(type: "TEXT", nullable: true),
                    BackdropPath = table.Column<string>(type: "TEXT", nullable: true),
                    BackdropUrl = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieCollections", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MediaItems_CollectionId",
                table: "MediaItems",
                column: "CollectionId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieCollections_Provider_ProviderId",
                table: "MovieCollections",
                columns: new[] { "Provider", "ProviderId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_MediaItems_MovieCollections_CollectionId",
                table: "MediaItems",
                column: "CollectionId",
                principalTable: "MovieCollections",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MediaItems_MovieCollections_CollectionId",
                table: "MediaItems");

            migrationBuilder.DropTable(
                name: "MovieCollections");

            migrationBuilder.DropIndex(
                name: "IX_MediaItems_CollectionId",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "CollectionId",
                table: "MediaItems");
        }
    }
}
