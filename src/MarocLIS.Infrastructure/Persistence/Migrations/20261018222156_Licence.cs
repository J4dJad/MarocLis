using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarocLIS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Licence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Licences",
                schema: "cfg",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Content = table.Column<string>(type: "nvarchar(max)", maxLength: 400, nullable: false),
                    ImportedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    ImportedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ImportedByUserName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Workstation = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ReplacedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Licences", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Workstations",
                schema: "cfg",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MachineName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    LastUserName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    AppVersion = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Workstations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Licences_Imported",
                schema: "cfg",
                table: "Licences",
                column: "ImportedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Licences_Number_Version",
                schema: "cfg",
                table: "Licences",
                columns: new[] { "Number", "Version" });

            migrationBuilder.CreateIndex(
                name: "UX_Workstations_Machine",
                schema: "cfg",
                table: "Workstations",
                column: "MachineName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Licences",
                schema: "cfg");

            migrationBuilder.DropTable(
                name: "Workstations",
                schema: "cfg");
        }
    }
}
