using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocumentStorage.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVerifiedOperationRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceSnapshotJson",
                table: "Operations",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "OperationRecoveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Actor = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ObservationHash = table.Column<string>(type: "text", nullable: false),
                    ObservationJson = table.Column<string>(type: "text", nullable: false),
                    Decision = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    Outcome = table.Column<string>(type: "text", nullable: false),
                    AppliedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationRecoveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OperationRecoveries_Operations_OperationId",
                        column: x => x.OperationId,
                        principalTable: "Operations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OperationRecoveries_ExpiresAt",
                table: "OperationRecoveries",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_OperationRecoveries_OperationId_CreatedAt",
                table: "OperationRecoveries",
                columns: new[] { "OperationId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OperationRecoveries");

            migrationBuilder.DropColumn(
                name: "SourceSnapshotJson",
                table: "Operations");
        }
    }
}
