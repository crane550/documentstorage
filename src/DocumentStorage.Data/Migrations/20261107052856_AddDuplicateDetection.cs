using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocumentStorage.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDuplicateDetection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TextHash",
                table: "Entries",
                type: "text",
                nullable: false,
                defaultValue: "");

            // Documents already read get their fingerprint now; it must match LibraryService.TextHash.
            migrationBuilder.Sql("UPDATE \"Entries\" SET \"TextHash\" = encode(sha256(convert_to(\"ExtractedText\", 'UTF8')), 'hex') WHERE \"ExtractedText\" <> '';");

            migrationBuilder.CreateIndex(
                name: "IX_Entries_Size",
                table: "Entries",
                column: "Size");

            migrationBuilder.CreateIndex(
                name: "IX_Entries_TextHash",
                table: "Entries",
                column: "TextHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Entries_Size",
                table: "Entries");

            migrationBuilder.DropIndex(
                name: "IX_Entries_TextHash",
                table: "Entries");

            migrationBuilder.DropColumn(
                name: "TextHash",
                table: "Entries");
        }
    }
}
