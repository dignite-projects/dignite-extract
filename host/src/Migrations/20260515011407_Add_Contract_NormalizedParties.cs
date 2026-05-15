using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dignite.Paperbase.Host.Migrations
{
    /// <inheritdoc />
    public partial class Add_Contract_NormalizedParties : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NormalizedPartyAName",
                table: "PaperbaseContracts",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NormalizedPartyBName",
                table: "PaperbaseContracts",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            // 硬伤二 (L2 Phase 3) best-effort backfill — applies the TRIM portion of
            // NormalizeEntityName (which is `NFKC + collapse-inner-whitespace + trim`).
            // NFKC pass and inner-whitespace collapse require CLR; rows with full-width
            // spaces or duplicated inner whitespace remain with the trimmed-only form
            // until the next AI re-extraction (ApplyFields / CorrectFields will then
            // write the fully-normalized form). SQL is SQL-92 compatible
            // (SQL Server / PostgreSQL / SQLite).
            migrationBuilder.Sql(@"
UPDATE PaperbaseContracts
SET NormalizedPartyAName = TRIM(PartyAName)
WHERE PartyAName IS NOT NULL AND TRIM(PartyAName) <> '';

UPDATE PaperbaseContracts
SET NormalizedPartyBName = TRIM(PartyBName)
WHERE PartyBName IS NOT NULL AND TRIM(PartyBName) <> '';");

            migrationBuilder.CreateIndex(
                name: "IX_PaperbaseContracts_NormalizedPartyAName_NormalizedPartyBName",
                table: "PaperbaseContracts",
                columns: new[] { "NormalizedPartyAName", "NormalizedPartyBName" },
                filter: "NormalizedPartyAName IS NOT NULL AND NormalizedPartyBName IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PaperbaseContracts_NormalizedPartyAName_NormalizedPartyBName",
                table: "PaperbaseContracts");

            migrationBuilder.DropColumn(
                name: "NormalizedPartyAName",
                table: "PaperbaseContracts");

            migrationBuilder.DropColumn(
                name: "NormalizedPartyBName",
                table: "PaperbaseContracts");
        }
    }
}
