using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dignite.DocumentAI.Host.Migrations
{
    /// <inheritdoc />
    public partial class Added_DocumentOriginBackReference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OriginDocumentId",
                table: "DocAIDocuments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginFigureKey",
                table: "DocAIDocuments",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DocAIDocuments_OriginDocumentId_OriginFigureKey",
                table: "DocAIDocuments",
                columns: new[] { "OriginDocumentId", "OriginFigureKey" },
                unique: true,
                filter: "[OriginDocumentId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DocAIDocuments_OriginDocumentId_OriginFigureKey",
                table: "DocAIDocuments");

            migrationBuilder.DropColumn(
                name: "OriginDocumentId",
                table: "DocAIDocuments");

            migrationBuilder.DropColumn(
                name: "OriginFigureKey",
                table: "DocAIDocuments");
        }
    }
}
