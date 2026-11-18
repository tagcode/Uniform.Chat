using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Uniform.Matrix.Client.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboxFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FilePath",
                table: "Outbox",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FilePath",
                table: "Outbox");
        }
    }
}
