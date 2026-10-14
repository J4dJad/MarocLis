using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarocLIS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SubcontractDispatch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SubcontractDispatchNumber",
                schema: "ord",
                table: "LabOrderItems",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SubcontractReturnedAt",
                schema: "ord",
                table: "LabOrderItems",
                type: "datetimeoffset(3)",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SubcontractSentByUserId",
                schema: "ord",
                table: "LabOrderItems",
                type: "uniqueidentifier",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SubcontractDispatchNumber",
                schema: "ord",
                table: "LabOrderItems");

            migrationBuilder.DropColumn(
                name: "SubcontractReturnedAt",
                schema: "ord",
                table: "LabOrderItems");

            migrationBuilder.DropColumn(
                name: "SubcontractSentByUserId",
                schema: "ord",
                table: "LabOrderItems");
        }
    }
}
