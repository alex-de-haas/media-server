using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaServer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProbeSourceProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Default 0 is ProbeSource.Engine, which is right for every existing row: until now the only
            // provider was a local ffprobe, so all stored media data came from one.
            migrationBuilder.AddColumn<int>(
                name: "ProbeSource",
                table: "MediaSources",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            // HdrFormat used to mean "the HDR name, or null for anything else", so a null said both "this
            // is not HDR" and "nobody looked". Those have to be told apart now that a provider exists which
            // genuinely cannot tell: a badge must stay silent about a file nobody could read rather than
            // claim it is SDR. Every existing row came from ffprobe, which reads the codec bitstream, so its
            // silence really was a negative — record that, and leave later nulls meaning unknown.
            migrationBuilder.Sql(
                "UPDATE MediaStreams SET HdrFormat = 'SDR' WHERE HdrFormat IS NULL AND StreamType = 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE MediaStreams SET HdrFormat = NULL WHERE HdrFormat = 'SDR';");

            migrationBuilder.DropColumn(
                name: "ProbeSource",
                table: "MediaSources");
        }
    }
}
