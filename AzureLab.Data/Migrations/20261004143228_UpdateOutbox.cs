using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzureLab.Data.Migrations
{
    /// <inheritdoc />
    public partial class UpdateOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Type",
                table: "OutboxMessages",
                newName: "Destination");

            migrationBuilder.RenameColumn(
                name: "ProcessedOnUtc",
                table: "OutboxMessages",
                newName: "ProcessedUtc");

            migrationBuilder.RenameColumn(
                name: "OccurredOnUtc",
                table: "OutboxMessages",
                newName: "CreatedUtc");

            migrationBuilder.RenameIndex(
                name: "IX_OutboxMessages_ProcessedOnUtc",
                table: "OutboxMessages",
                newName: "IX_OutboxMessages_ProcessedUtc");

            migrationBuilder.AddColumn<string>(
                name: "CorrelationId",
                table: "OutboxMessages",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Properties",
                table: "OutboxMessages",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Subject",
                table: "OutboxMessages",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CorrelationId",
                table: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "Properties",
                table: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "Subject",
                table: "OutboxMessages");

            migrationBuilder.RenameColumn(
                name: "ProcessedUtc",
                table: "OutboxMessages",
                newName: "ProcessedOnUtc");

            migrationBuilder.RenameColumn(
                name: "Destination",
                table: "OutboxMessages",
                newName: "Type");

            migrationBuilder.RenameColumn(
                name: "CreatedUtc",
                table: "OutboxMessages",
                newName: "OccurredOnUtc");

            migrationBuilder.RenameIndex(
                name: "IX_OutboxMessages_ProcessedUtc",
                table: "OutboxMessages",
                newName: "IX_OutboxMessages_ProcessedOnUtc");
        }
    }
}
