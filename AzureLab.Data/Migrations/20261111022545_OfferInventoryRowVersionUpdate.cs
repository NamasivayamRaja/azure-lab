using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzureLab.Data.Migrations
{
    /// <inheritdoc />
    public partial class OfferInventoryRowVersionUpdate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "OfferInventories");

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "OfferInventories",
                type: "rowversion",
                rowVersion: true,
                nullable: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "OfferInventories");

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "OfferInventories",
                type: "rowversion",
                rowVersion: true,
                nullable: false);
        }
    }
}
