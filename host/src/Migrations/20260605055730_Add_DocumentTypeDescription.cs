using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dignite.Paperbase.Host.Migrations
{
    /// <inheritdoc />
    public partial class Add_DocumentTypeDescription : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "PaperbaseDocumentTypes",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Description",
                table: "PaperbaseDocumentTypes");
        }
    }
}
