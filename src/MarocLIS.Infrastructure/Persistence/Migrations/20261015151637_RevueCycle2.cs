using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarocLIS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RevueCycle2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PreviousContractType",
                schema: "hr",
                table: "Employees",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PreviousContractType",
                schema: "hr",
                table: "Employees");
        }
    }
}
