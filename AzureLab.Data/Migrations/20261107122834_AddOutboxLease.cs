using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzureLab.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboxLease : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LockedUntilUtc",
                table: "OutboxMessages",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_ProcessedUtc_LockedUntilUtc_CreatedUtc",
                table: "OutboxMessages",
                columns: new[] { "ProcessedUtc", "LockedUntilUtc", "CreatedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OutboxMessages_ProcessedUtc_LockedUntilUtc_CreatedUtc",
                table: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "LockedUntilUtc",
                table: "OutboxMessages");
        }
    }
}
