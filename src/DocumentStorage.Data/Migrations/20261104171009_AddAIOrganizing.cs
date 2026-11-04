using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocumentStorage.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAIOrganizing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationFilingId",
                table: "Operations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OrganizationJson",
                table: "IdentificationJobs",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "Organizing",
                table: "IdentificationJobs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "OrganizationFilings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Actor = table.Column<string>(type: "text", nullable: false),
                    SourcePath = table.Column<string>(type: "text", nullable: false),
                    ContentHash = table.Column<string>(type: "text", nullable: false),
                    ContextHash = table.Column<string>(type: "text", nullable: false),
                    ReviewRevision = table.Column<int>(type: "integer", nullable: false),
                    SettingsRevision = table.Column<int>(type: "integer", nullable: false),
                    ParentFolderId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentPath = table.Column<string>(type: "text", nullable: false),
                    NewFolderName = table.Column<string>(type: "text", nullable: false),
                    TargetFolderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Filename = table.Column<string>(type: "text", nullable: false),
                    DestinationPath = table.Column<string>(type: "text", nullable: false),
                    DocumentType = table.Column<string>(type: "text", nullable: false),
                    FactsJson = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    Remember = table.Column<bool>(type: "boolean", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizationFilings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrganizationFilings_IdentificationJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "IdentificationJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FilingExamples",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RemovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RemovedBy = table.Column<string>(type: "text", nullable: false),
                    RootId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    FolderId = table.Column<Guid>(type: "uuid", nullable: false),
                    OriginalName = table.Column<string>(type: "text", nullable: false),
                    Filename = table.Column<string>(type: "text", nullable: false),
                    DocumentType = table.Column<string>(type: "text", nullable: false),
                    Summary = table.Column<string>(type: "text", nullable: false),
                    Note = table.Column<string>(type: "text", nullable: false),
                    Actor = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FilingExamples", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FilingExamples_OrganizationFilings_Id",
                        column: x => x.Id,
                        principalTable: "OrganizationFilings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Operations_OrganizationFilingId",
                table: "Operations",
                column: "OrganizationFilingId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FilingExamples_RootId_CreatedAt",
                table: "FilingExamples",
                columns: new[] { "RootId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationFilings_EntryId_CreatedAt",
                table: "OrganizationFilings",
                columns: new[] { "EntryId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationFilings_JobId",
                table: "OrganizationFilings",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationFilings_OperationId",
                table: "OrganizationFilings",
                column: "OperationId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Operations_OrganizationFilings_OrganizationFilingId",
                table: "Operations",
                column: "OrganizationFilingId",
                principalTable: "OrganizationFilings",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Operations_OrganizationFilings_OrganizationFilingId",
                table: "Operations");

            migrationBuilder.DropTable(
                name: "FilingExamples");

            migrationBuilder.DropTable(
                name: "OrganizationFilings");

            migrationBuilder.DropIndex(
                name: "IX_Operations_OrganizationFilingId",
                table: "Operations");

            migrationBuilder.DropColumn(
                name: "OrganizationFilingId",
                table: "Operations");

            migrationBuilder.DropColumn(
                name: "OrganizationJson",
                table: "IdentificationJobs");

            migrationBuilder.DropColumn(
                name: "Organizing",
                table: "IdentificationJobs");
        }
    }
}
