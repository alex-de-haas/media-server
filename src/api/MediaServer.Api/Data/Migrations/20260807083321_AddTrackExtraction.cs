using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaServer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTrackExtraction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "OutputPath",
                table: "TranscodeJobs",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "TranscodeJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "TranscodeJobOutputs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TranscodeJobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceStreamIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    RelativePath = table.Column<string>(type: "TEXT", nullable: false),
                    StreamType = table.Column<int>(type: "INTEGER", nullable: false),
                    Language = table.Column<string>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TranscodeJobOutputs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TranscodeJobOutputs_TranscodeJobs_TranscodeJobId",
                        column: x => x.TranscodeJobId,
                        principalTable: "TranscodeJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TranscodeJobOutputs_TranscodeJobId",
                table: "TranscodeJobOutputs",
                column: "TranscodeJobId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TranscodeJobOutputs");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "TranscodeJobs");

            migrationBuilder.AlterColumn<string>(
                name: "OutputPath",
                table: "TranscodeJobs",
                type: "TEXT",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true);
        }
    }
}
