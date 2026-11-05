using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocumentStorage.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRegulars : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ConfirmedAt",
                table: "OrganizationFilings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Routes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    KindId = table.Column<Guid>(type: "uuid", nullable: false),
                    CorrespondentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Approved = table.Column<int>(type: "integer", nullable: false),
                    Corrected = table.Column<int>(type: "integer", nullable: false),
                    Streak = table.Column<int>(type: "integer", nullable: false),
                    AutoFile = table.Column<bool>(type: "boolean", nullable: false),
                    NudgeAtStreak = table.Column<int>(type: "integer", nullable: false),
                    RecentJson = table.Column<string>(type: "text", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Routes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Routes_Correspondents_CorrespondentId",
                        column: x => x.CorrespondentId,
                        principalTable: "Correspondents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Routes_Kinds_KindId",
                        column: x => x.KindId,
                        principalTable: "Kinds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Routes_CorrespondentId",
                table: "Routes",
                column: "CorrespondentId");

            migrationBuilder.CreateIndex(
                name: "IX_Routes_KindId_CorrespondentId",
                table: "Routes",
                columns: new[] { "KindId", "CorrespondentId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Routes");

            migrationBuilder.DropColumn(
                name: "ConfirmedAt",
                table: "OrganizationFilings");
        }
    }
}
