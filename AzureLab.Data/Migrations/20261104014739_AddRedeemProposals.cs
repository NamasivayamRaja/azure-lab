using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzureLab.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRedeemProposals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RedeemProposals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    OfferId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ClaimedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ConfirmedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RedeemTransactionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RedeemProposals", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RedeemProposals_ExpiresUtc",
                table: "RedeemProposals",
                column: "ExpiresUtc");

            migrationBuilder.CreateIndex(
                name: "IX_RedeemProposals_UserId_OfferId",
                table: "RedeemProposals",
                columns: new[] { "UserId", "OfferId" },
                unique: true,
                filter: "[Status] = 'Pending'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RedeemProposals");
        }
    }
}
