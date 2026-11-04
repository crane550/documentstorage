using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace DocumentStorage.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentConversationsAndFolderPlans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PassageRevision",
                table: "Entries",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "DeskConversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Owner = table.Column<string>(type: "text", nullable: false),
                    Mode = table.Column<string>(type: "text", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    ScopeEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    Archived = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeskConversations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DocumentPassages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    Start = table.Column<int>(type: "integer", nullable: false),
                    Page = table.Column<int>(type: "integer", nullable: true),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    SourceRevision = table.Column<string>(type: "text", nullable: false),
                    Search = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: false)
                        .Annotation("Npgsql:TsVectorConfig", "english")
                        .Annotation("Npgsql:TsVectorProperties", new[] { "Title", "Text" })
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentPassages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DocumentPassages_Entries_EntryId",
                        column: x => x.EntryId,
                        principalTable: "Entries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeskTurns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestKey = table.Column<string>(type: "text", nullable: false),
                    Question = table.Column<string>(type: "text", nullable: false),
                    Reply = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    SettingsRevision = table.Column<int>(type: "integer", nullable: false),
                    ContextJson = table.Column<string>(type: "text", nullable: false),
                    ContextHash = table.Column<string>(type: "text", nullable: false),
                    ResultJson = table.Column<string>(type: "text", nullable: false),
                    Error = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeskTurns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeskTurns_DeskConversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "DeskConversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FolderPlanApprovals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TurnId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScopeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Actor = table.Column<string>(type: "text", nullable: false),
                    ExpectedHash = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    Error = table.Column<string>(type: "text", nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FolderPlanApprovals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FolderPlanApprovals_DeskTurns_TurnId",
                        column: x => x.TurnId,
                        principalTable: "DeskTurns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FolderPlanSteps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovalId = table.Column<Guid>(type: "uuid", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    RootId = table.Column<Guid>(type: "uuid", nullable: false),
                    FolderId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Path = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Instructions = table.Column<string>(type: "text", nullable: false),
                    Create = table.Column<bool>(type: "boolean", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FolderPlanSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FolderPlanSteps_FolderPlanApprovals_ApprovalId",
                        column: x => x.ApprovalId,
                        principalTable: "FolderPlanApprovals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeskConversations_Owner_UpdatedAt",
                table: "DeskConversations",
                columns: new[] { "Owner", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DeskTurns_ConversationId_RequestKey",
                table: "DeskTurns",
                columns: new[] { "ConversationId", "RequestKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeskTurns_State_CreatedAt",
                table: "DeskTurns",
                columns: new[] { "State", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentPassages_EntryId_Ordinal",
                table: "DocumentPassages",
                columns: new[] { "EntryId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DocumentPassages_Search",
                table: "DocumentPassages",
                column: "Search")
                .Annotation("Npgsql:IndexMethod", "GIN");

            migrationBuilder.CreateIndex(
                name: "IX_FolderPlanApprovals_TurnId",
                table: "FolderPlanApprovals",
                column: "TurnId");

            migrationBuilder.CreateIndex(
                name: "IX_FolderPlanSteps_ApprovalId_Ordinal",
                table: "FolderPlanSteps",
                columns: new[] { "ApprovalId", "Ordinal" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DocumentPassages");

            migrationBuilder.DropTable(
                name: "FolderPlanSteps");

            migrationBuilder.DropTable(
                name: "FolderPlanApprovals");

            migrationBuilder.DropTable(
                name: "DeskTurns");

            migrationBuilder.DropTable(
                name: "DeskConversations");

            migrationBuilder.DropColumn(
                name: "PassageRevision",
                table: "Entries");
        }
    }
}
