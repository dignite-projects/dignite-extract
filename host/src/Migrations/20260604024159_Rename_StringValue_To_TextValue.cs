using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dignite.Paperbase.Host.Migrations
{
    /// <inheritdoc />
    public partial class Rename_StringValue_To_TextValue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "StringValue",
                table: "PaperbaseDocumentExtractedFields",
                newName: "TextValue");

            migrationBuilder.RenameIndex(
                name: "IX_PaperbaseDocumentExtractedFields_TenantId_FieldDefinitionId_StringValue_DocumentId",
                table: "PaperbaseDocumentExtractedFields",
                newName: "IX_PaperbaseDocumentExtractedFields_TenantId_FieldDefinitionId_TextValue_DocumentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "TextValue",
                table: "PaperbaseDocumentExtractedFields",
                newName: "StringValue");

            migrationBuilder.RenameIndex(
                name: "IX_PaperbaseDocumentExtractedFields_TenantId_FieldDefinitionId_TextValue_DocumentId",
                table: "PaperbaseDocumentExtractedFields",
                newName: "IX_PaperbaseDocumentExtractedFields_TenantId_FieldDefinitionId_StringValue_DocumentId");
        }
    }
}
