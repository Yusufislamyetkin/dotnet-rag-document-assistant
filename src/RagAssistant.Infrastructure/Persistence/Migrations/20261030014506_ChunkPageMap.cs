using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RagAssistant.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChunkPageMap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int[]>(
                name: "page_numbers",
                table: "document_chunks",
                type: "integer[]",
                nullable: false,
                defaultValue: new int[0]);

            migrationBuilder.AddColumn<int[]>(
                name: "page_offsets",
                table: "document_chunks",
                type: "integer[]",
                nullable: false,
                defaultValue: new int[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "page_numbers",
                table: "document_chunks");

            migrationBuilder.DropColumn(
                name: "page_offsets",
                table: "document_chunks");
        }
    }
}
