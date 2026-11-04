using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocumentStorage.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddIntakeAndFilingRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "FilingPreviewId",
                table: "Operations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContentHash",
                table: "Entries",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "IsIntake",
                table: "Entries",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ObservedAt",
                table: "Entries",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<string>(
                name: "Readiness",
                table: "Entries",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "FilingPreviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    RuleId = table.Column<Guid>(type: "uuid", nullable: false),
                    RuleRevision = table.Column<int>(type: "integer", nullable: false),
                    ReviewRevision = table.Column<int>(type: "integer", nullable: false),
                    DestinationFolderId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourcePath = table.Column<string>(type: "text", nullable: false),
                    DestinationPath = table.Column<string>(type: "text", nullable: false),
                    Filename = table.Column<string>(type: "text", nullable: false),
                    ContentHash = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: true),
                    AppliedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FilingPreviews", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FilingRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    DocumentType = table.Column<string>(type: "text", nullable: false),
                    DestinationFolderId = table.Column<Guid>(type: "uuid", nullable: false),
                    MatchJson = table.Column<string>(type: "text", nullable: false),
                    FilenameTemplate = table.Column<string>(type: "text", nullable: false),
                    RequiredFactsJson = table.Column<string>(type: "text", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FilingRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FilingRules_Entries_DestinationFolderId",
                        column: x => x.DestinationFolderId,
                        principalTable: "Entries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Reviews",
                columns: table => new
                {
                    EntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentType = table.Column<string>(type: "text", nullable: false),
                    FactsJson = table.Column<string>(type: "text", nullable: false),
                    ContentHash = table.Column<string>(type: "text", nullable: false),
                    Reviewed = table.Column<bool>(type: "boolean", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reviews", x => x.EntryId);
                    table.ForeignKey(
                        name: "FK_Reviews_Entries_EntryId",
                        column: x => x.EntryId,
                        principalTable: "Entries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RuleRevisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RuleId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    SnapshotJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuleRevisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RuleRevisions_FilingRules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "FilingRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Operations_FilingPreviewId",
                table: "Operations",
                column: "FilingPreviewId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FilingPreviews_EntryId",
                table: "FilingPreviews",
                column: "EntryId");

            migrationBuilder.CreateIndex(
                name: "IX_FilingRules_DestinationFolderId",
                table: "FilingRules",
                column: "DestinationFolderId");

            migrationBuilder.CreateIndex(
                name: "IX_RuleRevisions_RuleId_Revision",
                table: "RuleRevisions",
                columns: new[] { "RuleId", "Revision" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FilingPreviews");

            migrationBuilder.DropTable(
                name: "Reviews");

            migrationBuilder.DropTable(
                name: "RuleRevisions");

            migrationBuilder.DropTable(
                name: "FilingRules");

            migrationBuilder.DropIndex(
                name: "IX_Operations_FilingPreviewId",
                table: "Operations");

            migrationBuilder.DropColumn(
                name: "FilingPreviewId",
                table: "Operations");

            migrationBuilder.DropColumn(
                name: "ContentHash",
                table: "Entries");

            migrationBuilder.DropColumn(
                name: "IsIntake",
                table: "Entries");

            migrationBuilder.DropColumn(
                name: "ObservedAt",
                table: "Entries");

            migrationBuilder.DropColumn(
                name: "Readiness",
                table: "Entries");
        }
    }
}
