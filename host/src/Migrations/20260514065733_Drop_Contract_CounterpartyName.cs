using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dignite.Paperbase.Host.Migrations
{
    /// <inheritdoc />
    public partial class Drop_Contract_CounterpartyName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PaperbaseContracts_CounterpartyName",
                table: "PaperbaseContracts");

            migrationBuilder.DropColumn(
                name: "CounterpartyName",
                table: "PaperbaseContracts");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CounterpartyName",
                table: "PaperbaseContracts",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaperbaseContracts_CounterpartyName",
                table: "PaperbaseContracts",
                column: "CounterpartyName");
        }
    }
}
