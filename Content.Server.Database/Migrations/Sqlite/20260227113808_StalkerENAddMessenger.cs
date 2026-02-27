using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Content.Server.Database.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class StalkerENAddMessenger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "stalker_messenger_contacts",
                columns: table => new
                {
                    owner_character_name = table.Column<string>(type: "TEXT", nullable: false),
                    contact_character_name = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stalker_messenger_contacts", x => new { x.owner_character_name, x.contact_character_name });
                });

            migrationBuilder.CreateTable(
                name: "stalker_messenger_ids",
                columns: table => new
                {
                    character_name = table.Column<string>(type: "TEXT", nullable: false),
                    messenger_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stalker_messenger_ids", x => x.character_name);
                });

            migrationBuilder.CreateIndex(
                name: "IX_stalker_messenger_ids_messenger_id",
                table: "stalker_messenger_ids",
                column: "messenger_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "stalker_messenger_contacts");

            migrationBuilder.DropTable(
                name: "stalker_messenger_ids");
        }
    }
}
