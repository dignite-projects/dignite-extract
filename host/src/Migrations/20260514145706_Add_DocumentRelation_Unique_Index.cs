using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dignite.Paperbase.Host.Migrations
{
    /// <inheritdoc />
    public partial class Add_DocumentRelation_Unique_Index : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_PaperbaseDocumentRelations_TenantId_SourceDocumentId_TargetDocumentId",
                table: "PaperbaseDocumentRelations",
                columns: new[] { "TenantId", "SourceDocumentId", "TargetDocumentId" },
                unique: true,
                filter: "IsDeleted = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PaperbaseDocumentRelations_TenantId_SourceDocumentId_TargetDocumentId",
                table: "PaperbaseDocumentRelations");
        }
    }
}
