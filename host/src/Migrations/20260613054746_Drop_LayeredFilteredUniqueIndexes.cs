using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dignite.DocumentAI.Host.Migrations
{
    /// <inheritdoc />
    public partial class Drop_LayeredFilteredUniqueIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DocAIFieldDefinitions_TenantId_DocumentTypeId_Name",
                table: "DocAIFieldDefinitions");

            migrationBuilder.DropIndex(
                name: "IX_DocAIExportTemplates_TenantId_Name",
                table: "DocAIExportTemplates");

            migrationBuilder.DropIndex(
                name: "IX_DocAIDocumentTypes_TenantId_TypeCode",
                table: "DocAIDocumentTypes");

            migrationBuilder.DropIndex(
                name: "IX_DocAICabinets_TenantId_Name",
                table: "DocAICabinets");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_DocAIFieldDefinitions_TenantId_DocumentTypeId_Name",
                table: "DocAIFieldDefinitions",
                columns: new[] { "TenantId", "DocumentTypeId", "Name" },
                unique: true,
                filter: "IsDeleted = 0");

            migrationBuilder.CreateIndex(
                name: "IX_DocAIExportTemplates_TenantId_Name",
                table: "DocAIExportTemplates",
                columns: new[] { "TenantId", "Name" },
                unique: true,
                filter: "IsDeleted = 0");

            migrationBuilder.CreateIndex(
                name: "IX_DocAIDocumentTypes_TenantId_TypeCode",
                table: "DocAIDocumentTypes",
                columns: new[] { "TenantId", "TypeCode" },
                unique: true,
                filter: "IsDeleted = 0");

            migrationBuilder.CreateIndex(
                name: "IX_DocAICabinets_TenantId_Name",
                table: "DocAICabinets",
                columns: new[] { "TenantId", "Name" },
                unique: true,
                filter: "IsDeleted = 0");
        }
    }
}
