using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Uniform.Matrix.Server.Storage.Migrations
{
    /// <inheritdoc />
    public partial class ScopeTransactionsToRooms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_Transactions",
                table: "Transactions");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Transactions",
                table: "Transactions",
                columns: new[] { "UserId", "DeviceId", "TransactionId", "EventId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_Transactions",
                table: "Transactions");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Transactions",
                table: "Transactions",
                columns: new[] { "UserId", "DeviceId", "TransactionId" });
        }
    }
}
