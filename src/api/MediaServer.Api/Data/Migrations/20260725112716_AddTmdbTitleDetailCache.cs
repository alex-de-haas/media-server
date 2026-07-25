using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaServer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTmdbTitleDetailCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TmdbTitleDetailCache",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    TmdbId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Language = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Payload = table.Column<string>(type: "TEXT", nullable: false),
                    FetchedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TmdbTitleDetailCache", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TmdbTitleDetailCache_Kind_TmdbId_Language",
                table: "TmdbTitleDetailCache",
                columns: new[] { "Kind", "TmdbId", "Language" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TmdbTitleDetailCache");
        }
    }
}
