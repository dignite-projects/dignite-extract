using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dignite.Paperbase.Host.Migrations
{
    /// <inheritdoc />
    public partial class Move_OriginalFileBlobName_Into_FileOrigin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "OriginalFileBlobName",
                table: "PaperbaseDocuments",
                newName: "FileOrigin_BlobName");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "FileOrigin_BlobName",
                table: "PaperbaseDocuments",
                newName: "OriginalFileBlobName");
        }
    }
}
