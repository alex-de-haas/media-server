using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaServer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPeople : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Persons",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", nullable: false),
                    ProviderId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    ProfilePath = table.Column<string>(type: "TEXT", nullable: true),
                    ProfileUrl = table.Column<string>(type: "TEXT", nullable: true),
                    Biography = table.Column<string>(type: "TEXT", nullable: true),
                    KnownForDepartment = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Persons", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MediaItemPersons",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MediaItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PersonId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Role = table.Column<int>(type: "INTEGER", nullable: false),
                    Character = table.Column<string>(type: "TEXT", nullable: true),
                    Job = table.Column<string>(type: "TEXT", nullable: true),
                    Department = table.Column<string>(type: "TEXT", nullable: true),
                    Order = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaItemPersons", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MediaItemPersons_MediaItems_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MediaItemPersons_Persons_PersonId",
                        column: x => x.PersonId,
                        principalTable: "Persons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MediaItemPersons_MediaItemId",
                table: "MediaItemPersons",
                column: "MediaItemId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaItemPersons_PersonId",
                table: "MediaItemPersons",
                column: "PersonId");

            migrationBuilder.CreateIndex(
                name: "IX_Persons_Provider_ProviderId",
                table: "Persons",
                columns: new[] { "Provider", "ProviderId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MediaItemPersons");

            migrationBuilder.DropTable(
                name: "Persons");
        }
    }
}
