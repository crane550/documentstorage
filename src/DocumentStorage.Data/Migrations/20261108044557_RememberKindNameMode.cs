using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocumentStorage.Data.Migrations
{
    /// <inheritdoc />
    public partial class RememberKindNameMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "KeepOriginalName",
                table: "Kinds",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "KeepOriginalName",
                table: "Kinds");
        }
    }
}
