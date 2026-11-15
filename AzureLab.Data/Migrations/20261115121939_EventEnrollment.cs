using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzureLab.Data.Migrations
{
    /// <inheritdoc />
    public partial class EventEnrollment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EventEnrollments",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    EnrolledUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventEnrollments", x => new { x.EventId, x.UserId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_EventEnrollments_UserId",
                table: "EventEnrollments",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EventEnrollments");
        }
    }
}
