using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaServer.Api.Data.Migrations
{
    /// <summary>
    /// Replaces the per-job CRF with the encoder-independent quality level, carrying the existing values
    /// across rather than dropping them: a finished job's settings are the only record of what produced the
    /// file sitting beside it, and a job list that forgets them stops explaining itself.
    /// <para>
    /// The buckets come from the levels' own CRF values, so a job that asked for exactly what a level means
    /// lands on that level and the boundaries sit between them. H.264 gets its own set, because x264 needs a
    /// CRF two points lower than x265 for the same picture — reading an H.264 job's CRF 20 as "high" would
    /// credit it with a quality it never had.
    /// </para>
    /// <para>
    /// A null CRF stays null. Those rows are either a video copy — where null is what the new column means
    /// anyway — or an encode from before levels existed, which simply took the encoder's own default;
    /// naming a level for them would invent a choice nobody made.
    /// </para>
    /// </summary>
    public partial class ReplaceCrfWithQualityLevel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "QualityLevel",
                table: "TranscodeJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReEncodedAudioTracks",
                table: "TranscodeJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            // Between the two AddColumns and the DropColumn on purpose: the new column has to exist to be
            // written, and the old one has to still be there to be read.
            migrationBuilder.Sql("""
                UPDATE "TranscodeJobs"
                SET "QualityLevel" =
                    CASE WHEN "VideoCodec" = 'h264' THEN
                        CASE
                            WHEN "Crf" <= 17 THEN 'highest'
                            WHEN "Crf" <= 19 THEN 'high'
                            WHEN "Crf" <= 21 THEN 'balanced'
                            ELSE 'small'
                        END
                    ELSE
                        CASE
                            WHEN "Crf" <= 19 THEN 'highest'
                            WHEN "Crf" <= 21 THEN 'high'
                            WHEN "Crf" <= 23 THEN 'balanced'
                            ELSE 'small'
                        END
                    END
                WHERE "Crf" IS NOT NULL;
                """);

            migrationBuilder.DropColumn(
                name: "Crf",
                table: "TranscodeJobs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Crf",
                table: "TranscodeJobs",
                type: "INTEGER",
                nullable: true);

            // Lossy by nature — a level is a bucket, not a number — so this writes the CRF each level is
            // defined as, which is the one value that maps back to the same level going the other way.
            migrationBuilder.Sql("""
                UPDATE "TranscodeJobs"
                SET "Crf" =
                    CASE WHEN "VideoCodec" = 'h264' THEN
                        CASE "QualityLevel"
                            WHEN 'highest' THEN 16
                            WHEN 'high' THEN 18
                            WHEN 'balanced' THEN 20
                            ELSE 22
                        END
                    ELSE
                        CASE "QualityLevel"
                            WHEN 'highest' THEN 18
                            WHEN 'high' THEN 20
                            WHEN 'balanced' THEN 22
                            ELSE 24
                        END
                    END
                WHERE "QualityLevel" IS NOT NULL;
                """);

            migrationBuilder.DropColumn(
                name: "QualityLevel",
                table: "TranscodeJobs");

            migrationBuilder.DropColumn(
                name: "ReEncodedAudioTracks",
                table: "TranscodeJobs");
        }
    }
}
