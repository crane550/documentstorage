using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocumentStorage.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRefiling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "FollowsFolder",
                table: "Details",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "FollowsName",
                table: "Details",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Documents already filed by sorting followed their kind's patterns; a name kept from the
            // original file did not.
            migrationBuilder.Sql("""
                UPDATE "Details" AS d
                SET "FollowsFolder" = TRUE,
                    "FollowsName" = (f."Filename" <> regexp_replace(f."SourcePath", '^.*/', ''))
                FROM "OrganizationFilings" AS f
                WHERE f."EntryId" = d."EntryId" AND f."DetailsJson" <> '' AND f."State" = 'Filed' AND f."UndoneAt" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FollowsFolder",
                table: "Details");

            migrationBuilder.DropColumn(
                name: "FollowsName",
                table: "Details");
        }
    }
}
