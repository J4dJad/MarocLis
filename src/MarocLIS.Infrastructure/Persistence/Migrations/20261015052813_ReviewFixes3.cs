using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarocLIS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReviewFixes3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RepeatedAt",
                schema: "ord",
                table: "LabOrderItems",
                type: "datetimeoffset(3)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PreviousBaseSalary",
                schema: "hr",
                table: "Employees",
                type: "decimal(18,6)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RepeatedAt",
                schema: "ord",
                table: "LabOrderItems");

            migrationBuilder.DropColumn(
                name: "PreviousBaseSalary",
                schema: "hr",
                table: "Employees");
        }
    }
}
