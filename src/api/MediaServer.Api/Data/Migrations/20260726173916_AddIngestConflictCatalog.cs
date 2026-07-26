using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaServer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddIngestConflictCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ConflictCatalogId",
                table: "IngestItems",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConflictCatalogId",
                table: "IngestItems");
        }
    }
}
