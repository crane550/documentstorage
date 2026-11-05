using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocumentStorage.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentDetailsAndSorting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "JobId",
                table: "OrganizationFilings",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<bool>(
                name: "Automatic",
                table: "OrganizationFilings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "DetailsJson",
                table: "OrganizationFilings",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UndoneAt",
                table: "OrganizationFilings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AutoFileEnabled",
                table: "OllamaConfigurations",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "Sorting",
                table: "IdentificationJobs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "Correspondents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    AliasesJson = table.Column<string>(type: "text", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Correspondents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Kinds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    FolderPattern = table.Column<string>(type: "text", nullable: false),
                    NamePattern = table.Column<string>(type: "text", nullable: false),
                    FieldsJson = table.Column<string>(type: "text", nullable: false),
                    IsCatchAll = table.Column<bool>(type: "boolean", nullable: false),
                    AutoFile = table.Column<bool>(type: "boolean", nullable: false),
                    Approved = table.Column<int>(type: "integer", nullable: false),
                    Corrected = table.Column<int>(type: "integer", nullable: false),
                    Streak = table.Column<int>(type: "integer", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Kinds", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tags",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    AliasesJson = table.Column<string>(type: "text", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tags", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Details",
                columns: table => new
                {
                    EntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    KindId = table.Column<Guid>(type: "uuid", nullable: true),
                    CorrespondentId = table.Column<Guid>(type: "uuid", nullable: true),
                    DocumentDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Title = table.Column<string>(type: "text", nullable: false),
                    FieldsJson = table.Column<string>(type: "text", nullable: false),
                    SourcesJson = table.Column<string>(type: "text", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Details", x => x.EntryId);
                    table.ForeignKey(
                        name: "FK_Details_Correspondents_CorrespondentId",
                        column: x => x.CorrespondentId,
                        principalTable: "Correspondents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Details_Entries_EntryId",
                        column: x => x.EntryId,
                        principalTable: "Entries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Details_Kinds_KindId",
                        column: x => x.KindId,
                        principalTable: "Kinds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "EntryTags",
                columns: table => new
                {
                    EntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    TagId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EntryTags", x => new { x.EntryId, x.TagId });
                    table.ForeignKey(
                        name: "FK_EntryTags_Entries_EntryId",
                        column: x => x.EntryId,
                        principalTable: "Entries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EntryTags_Tags_TagId",
                        column: x => x.TagId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Correspondents_Name",
                table: "Correspondents",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Details_CorrespondentId",
                table: "Details",
                column: "CorrespondentId");

            migrationBuilder.CreateIndex(
                name: "IX_Details_DocumentDate",
                table: "Details",
                column: "DocumentDate");

            migrationBuilder.CreateIndex(
                name: "IX_Details_KindId",
                table: "Details",
                column: "KindId");

            migrationBuilder.CreateIndex(
                name: "IX_EntryTags_TagId",
                table: "EntryTags",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_Kinds_Name",
                table: "Kinds",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tags_Name",
                table: "Tags",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Details");

            migrationBuilder.DropTable(
                name: "EntryTags");

            migrationBuilder.DropTable(
                name: "Correspondents");

            migrationBuilder.DropTable(
                name: "Kinds");

            migrationBuilder.DropTable(
                name: "Tags");

            migrationBuilder.DropColumn(
                name: "Automatic",
                table: "OrganizationFilings");

            migrationBuilder.DropColumn(
                name: "DetailsJson",
                table: "OrganizationFilings");

            migrationBuilder.DropColumn(
                name: "UndoneAt",
                table: "OrganizationFilings");

            migrationBuilder.DropColumn(
                name: "AutoFileEnabled",
                table: "OllamaConfigurations");

            migrationBuilder.DropColumn(
                name: "Sorting",
                table: "IdentificationJobs");

            migrationBuilder.AlterColumn<Guid>(
                name: "JobId",
                table: "OrganizationFilings",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
