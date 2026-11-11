using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocumentStorage.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddReadingLearning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Confirmed",
                table: "Details",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ReaderWords",
                table: "Details",
                type: "text",
                nullable: false,
                defaultValue: "");

            // Details a person approved or set by hand are evidence for later readings. Ones that
            // filed themselves count once someone said "Looks right"; an undone filing does not.
            migrationBuilder.Sql("""
                UPDATE "Details" AS d SET "Confirmed" = TRUE
                WHERE d."KindId" IS NOT NULL AND d."UpdatedBy" <> 'automatic' AND d."UpdatedBy" NOT LIKE 'assistant:%'
                  AND NOT EXISTS (SELECT 1 FROM "OrganizationFilings" AS f WHERE f."EntryId" = d."EntryId" AND f."UndoneAt" IS NOT NULL
                      AND NOT EXISTS (SELECT 1 FROM "OrganizationFilings" AS later WHERE later."EntryId" = f."EntryId" AND later."State" = 'Filed'
                          AND later."UndoneAt" IS NULL AND later."CreatedAt" > f."CreatedAt"));
                UPDATE "Details" AS d SET "Confirmed" = TRUE
                FROM "OrganizationFilings" AS f
                WHERE f."EntryId" = d."EntryId" AND d."KindId" IS NOT NULL AND f."ConfirmedAt" IS NOT NULL AND f."UndoneAt" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Confirmed",
                table: "Details");

            migrationBuilder.DropColumn(
                name: "ReaderWords",
                table: "Details");
        }
    }
}
