using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzureLab.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProcessedMessage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProcessedMessages",
                columns: table => new
                {
                    Consumer = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    MessageId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ProcessedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessedMessages", x => new { x.Consumer, x.MessageId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProcessedMessages_ProcessedUtc",
                table: "ProcessedMessages",
                column: "ProcessedUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProcessedMessages");
        }
    }
}
