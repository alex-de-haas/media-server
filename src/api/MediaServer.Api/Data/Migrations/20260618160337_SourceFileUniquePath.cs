using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaServer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class SourceFileUniquePath : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SourceFiles_DownloadId",
                table: "SourceFiles");

            // Collapse any pre-existing duplicate (DownloadId, RelativePath) rows left by the old
            // concurrent-upsert race before the unique index is created (it would otherwise fail).
            // Keep the assigned row if there is one, else the earliest.
            migrationBuilder.Sql(@"
                DELETE FROM ""SourceFiles""
                WHERE ""Id"" IN (
                    SELECT ""Id"" FROM (
                        SELECT ""Id"", ROW_NUMBER() OVER (
                            PARTITION BY ""DownloadId"", ""RelativePath""
                            ORDER BY CASE WHEN ""MediaItemId"" IS NULL THEN 1 ELSE 0 END, ""CreatedAt"", ""Id""
                        ) AS rn
                        FROM ""SourceFiles""
                    ) ranked
                    WHERE ranked.rn > 1
                );");

            migrationBuilder.CreateIndex(
                name: "IX_SourceFiles_DownloadId_RelativePath",
                table: "SourceFiles",
                columns: new[] { "DownloadId", "RelativePath" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SourceFiles_DownloadId_RelativePath",
                table: "SourceFiles");

            migrationBuilder.CreateIndex(
                name: "IX_SourceFiles_DownloadId",
                table: "SourceFiles",
                column: "DownloadId");
        }
    }
}
