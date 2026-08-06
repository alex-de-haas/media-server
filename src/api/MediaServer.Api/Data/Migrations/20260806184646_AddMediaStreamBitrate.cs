using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaServer.Api.Data.Migrations
{
    /// <summary>
    /// Records what one track costs, so the convert dialog can say what dropping or re-encoding it is worth
    /// rather than only what the whole file weighs.
    /// <para>
    /// Every existing row starts null and stays null. The figure comes from the engine probe, and there is
    /// nothing already in the database to derive it from — a share of the source's overall bitrate would be
    /// a guess wearing a measurement's clothes. A library filled in before this column existed answers it
    /// once "Refresh media data" re-probes the item; until then the dialog simply shows no estimate.
    /// </para>
    /// </summary>
    public partial class AddMediaStreamBitrate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Bitrate",
                table: "MediaStreams",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Bitrate",
                table: "MediaStreams");
        }
    }
}
