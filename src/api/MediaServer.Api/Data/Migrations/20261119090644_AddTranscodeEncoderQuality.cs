using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaServer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTranscodeEncoderQuality : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "EncoderQuality",
                table: "TranscodeJobs",
                type: "REAL",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EncoderQuality",
                table: "TranscodeJobs");
        }
    }
}
