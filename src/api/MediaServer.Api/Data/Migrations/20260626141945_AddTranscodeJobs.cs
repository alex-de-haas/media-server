using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaServer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTranscodeJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TranscodeJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EngineJobId = table.Column<string>(type: "TEXT", nullable: false),
                    MediaSourceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MediaItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CatalogId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    InputPath = table.Column<string>(type: "TEXT", nullable: false),
                    OutputPath = table.Column<string>(type: "TEXT", nullable: false),
                    VideoCodec = table.Column<string>(type: "TEXT", nullable: false),
                    HardwareAcceleration = table.Column<string>(type: "TEXT", nullable: false),
                    Crf = table.Column<int>(type: "INTEGER", nullable: true),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    PercentComplete = table.Column<double>(type: "REAL", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TranscodeJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TranscodeJobs_Catalogs_CatalogId",
                        column: x => x.CatalogId,
                        principalTable: "Catalogs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TranscodeJobs_MediaItems_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TranscodeJobs_MediaSources_MediaSourceId",
                        column: x => x.MediaSourceId,
                        principalTable: "MediaSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TranscodeJobs_CatalogId",
                table: "TranscodeJobs",
                column: "CatalogId");

            migrationBuilder.CreateIndex(
                name: "IX_TranscodeJobs_EngineJobId",
                table: "TranscodeJobs",
                column: "EngineJobId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TranscodeJobs_MediaItemId",
                table: "TranscodeJobs",
                column: "MediaItemId");

            migrationBuilder.CreateIndex(
                name: "IX_TranscodeJobs_MediaSourceId",
                table: "TranscodeJobs",
                column: "MediaSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_TranscodeJobs_State",
                table: "TranscodeJobs",
                column: "State");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TranscodeJobs");
        }
    }
}
