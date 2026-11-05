using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocumentStorage.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSavedDocumentDescriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DescriptionRevision",
                table: "IdentificationJobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "FromSavedDescription",
                table: "IdentificationJobs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "DocumentDescriptions",
                columns: table => new
                {
                    EntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentHash = table.Column<string>(type: "text", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Summary = table.Column<string>(type: "text", nullable: false),
                    Tags = table.Column<string[]>(type: "text[]", nullable: false),
                    Organizations = table.Column<string[]>(type: "text[]", nullable: false),
                    FactsJson = table.Column<string>(type: "text", nullable: false),
                    EvidenceJson = table.Column<string>(type: "text", nullable: false),
                    SearchText = table.Column<string>(type: "text", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    SourceJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    EditedByUser = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedBy = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentDescriptions", x => x.EntryId);
                    table.ForeignKey(
                        name: "FK_DocumentDescriptions_Entries_EntryId",
                        column: x => x.EntryId,
                        principalTable: "Entries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });
            // Reuse existing completed readings; deployment never invokes a model.
            migrationBuilder.Sql("""
                INSERT INTO "DocumentDescriptions" ("EntryId","ContentHash","Title","Summary","Tags","Organizations","FactsJson","EvidenceJson","SearchText","Revision","SourceJobId","EditedByUser","UpdatedBy","UpdatedAt")
                SELECT DISTINCT ON (j."EntryId") j."EntryId",j."ContentHash",e."Name",j."Summary",ARRAY[replace(j."DocumentType", '_',' ')],ARRAY[]::text[],j."FactsJson",j."EvidenceJson",
                    e."Name" || ' ' || j."Summary" || ' ' || replace(j."DocumentType", '_',' ') || ' ' || j."FactsJson",1,j."Id",false,'imported-saved-reading',COALESCE(j."CompletedAt",j."CreatedAt")
                FROM "IdentificationJobs" j JOIN "Entries" e ON e."Id"=j."EntryId"
                WHERE j."ContentHash"=e."ContentHash" AND j."ContentHash"<>'' AND j."Summary"<>''
                ORDER BY j."EntryId",j."CreatedAt" DESC,j."Id" DESC
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DocumentDescriptions");

            migrationBuilder.DropColumn(
                name: "DescriptionRevision",
                table: "IdentificationJobs");

            migrationBuilder.DropColumn(
                name: "FromSavedDescription",
                table: "IdentificationJobs");
        }
    }
}
