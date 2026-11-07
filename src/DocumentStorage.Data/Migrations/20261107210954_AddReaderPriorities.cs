using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocumentStorage.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddReaderPriorities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FallbackReadersJson",
                table: "OllamaConfigurations",
                type: "text",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<int>(
                name: "KeepAliveSeconds",
                table: "OllamaConfigurations",
                type: "integer",
                nullable: false,
                defaultValue: 120);

            migrationBuilder.AddColumn<string>(
                name: "ReaderName",
                table: "OllamaConfigurations",
                type: "text",
                nullable: false,
                defaultValue: "Primary reader");

            migrationBuilder.AddColumn<string>(
                name: "ReaderName",
                table: "IdentificationJobs",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FallbackReadersJson",
                table: "OllamaConfigurations");

            migrationBuilder.DropColumn(
                name: "KeepAliveSeconds",
                table: "OllamaConfigurations");

            migrationBuilder.DropColumn(
                name: "ReaderName",
                table: "OllamaConfigurations");

            migrationBuilder.DropColumn(
                name: "ReaderName",
                table: "IdentificationJobs");
        }
    }
}
