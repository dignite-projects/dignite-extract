using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dignite.Paperbase.Host.Migrations
{
    /// <inheritdoc />
    public partial class Add_DocumentExtractedField_Order_And_FieldDefinition_AllowMultiple : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_PaperbaseDocumentExtractedFields",
                table: "PaperbaseDocumentExtractedFields");

            migrationBuilder.AddColumn<bool>(
                name: "AllowMultiple",
                table: "PaperbaseFieldDefinitions",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "Order",
                table: "PaperbaseDocumentExtractedFields",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddPrimaryKey(
                name: "PK_PaperbaseDocumentExtractedFields",
                table: "PaperbaseDocumentExtractedFields",
                columns: new[] { "DocumentId", "FieldDefinitionId", "Order" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_PaperbaseDocumentExtractedFields",
                table: "PaperbaseDocumentExtractedFields");

            migrationBuilder.DropColumn(
                name: "AllowMultiple",
                table: "PaperbaseFieldDefinitions");

            migrationBuilder.DropColumn(
                name: "Order",
                table: "PaperbaseDocumentExtractedFields");

            migrationBuilder.AddPrimaryKey(
                name: "PK_PaperbaseDocumentExtractedFields",
                table: "PaperbaseDocumentExtractedFields",
                columns: new[] { "DocumentId", "FieldDefinitionId" });
        }
    }
}
