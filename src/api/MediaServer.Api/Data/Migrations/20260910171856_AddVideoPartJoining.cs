using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaServer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVideoPartJoining : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CancellationRequested",
                table: "TranscodeJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<double>(
                name: "ExpectedDurationSeconds",
                table: "TranscodeJobs",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastSubmissionAt",
                table: "TranscodeJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "OutputImported",
                table: "TranscodeJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SecondInputPath",
                table: "TranscodeJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SecondSourceId",
                table: "TranscodeJobs",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CancellationRequested",
                table: "TranscodeJobs");

            migrationBuilder.DropColumn(
                name: "ExpectedDurationSeconds",
                table: "TranscodeJobs");

            migrationBuilder.DropColumn(
                name: "LastSubmissionAt",
                table: "TranscodeJobs");

            migrationBuilder.DropColumn(
                name: "OutputImported",
                table: "TranscodeJobs");

            migrationBuilder.DropColumn(
                name: "SecondInputPath",
                table: "TranscodeJobs");

            migrationBuilder.DropColumn(
                name: "SecondSourceId",
                table: "TranscodeJobs");
        }
    }
}
