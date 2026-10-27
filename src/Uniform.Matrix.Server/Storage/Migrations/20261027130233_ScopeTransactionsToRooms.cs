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
            // SQLite cannot change a primary key in place; the table has no foreign keys, so a plain rebuild runs inside the migration's transaction
            migrationBuilder.Sql("""
                CREATE TABLE "ef_temp_Transactions" (
                    "UserId" TEXT NOT NULL, "DeviceId" TEXT NOT NULL, "TransactionId" TEXT NOT NULL, "EventId" TEXT NOT NULL,
                    CONSTRAINT "PK_Transactions" PRIMARY KEY ("UserId", "DeviceId", "TransactionId", "EventId"));
                INSERT INTO "ef_temp_Transactions" ("UserId", "DeviceId", "TransactionId", "EventId") SELECT "UserId", "DeviceId", "TransactionId", "EventId" FROM "Transactions";
                DROP TABLE "Transactions";
                ALTER TABLE "ef_temp_Transactions" RENAME TO "Transactions";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // SQLite cannot change a primary key in place; the table has no foreign keys, so a plain rebuild runs inside the migration's transaction
            migrationBuilder.Sql("""
                CREATE TABLE "ef_temp_Transactions" (
                    "UserId" TEXT NOT NULL, "DeviceId" TEXT NOT NULL, "TransactionId" TEXT NOT NULL, "EventId" TEXT NOT NULL,
                    CONSTRAINT "PK_Transactions" PRIMARY KEY ("UserId", "DeviceId", "TransactionId"));
                INSERT OR IGNORE INTO "ef_temp_Transactions" ("UserId", "DeviceId", "TransactionId", "EventId") SELECT "UserId", "DeviceId", "TransactionId", "EventId" FROM "Transactions";
                DROP TABLE "Transactions";
                ALTER TABLE "ef_temp_Transactions" RENAME TO "Transactions";
                """);
        }
    }
}
