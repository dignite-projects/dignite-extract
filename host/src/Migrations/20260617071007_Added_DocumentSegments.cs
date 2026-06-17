using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dignite.DocumentAI.Host.Migrations
{
    /// <inheritdoc />
    public partial class Added_DocumentSegments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DocAIDocumentSegments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourceDocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SegmentKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SliceText = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    RoutedDocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ExtraProperties = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ConcurrencyStamp = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    CreationTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocAIDocumentSegments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DocAIDocumentSegments_DocAIDocuments_SourceDocumentId",
                        column: x => x.SourceDocumentId,
                        principalTable: "DocAIDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DocAIDocumentSegments_SourceDocumentId_Ordinal",
                table: "DocAIDocumentSegments",
                columns: new[] { "SourceDocumentId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DocAIDocumentSegments_SourceDocumentId_SegmentKey",
                table: "DocAIDocumentSegments",
                columns: new[] { "SourceDocumentId", "SegmentKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DocAIDocumentSegments");
        }
    }
}
