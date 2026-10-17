using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarocLIS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NomenclatureActes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NationalBiologyActs",
                schema: "cat",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false, collation: "Latin1_General_100_CS_AS_SC_UTF8"),
                    Label = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false, collation: "Latin1_General_100_CI_AI_SC_UTF8"),
                    Coefficient = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    LetterKey = table.Column<string>(type: "nvarchar(5)", maxLength: 5, nullable: false),
                    Chapter = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SubChapter = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    DecreeYear = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NationalBiologyActs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "UX_NationalBiologyActs_Code",
                schema: "cat",
                table: "NationalBiologyActs",
                column: "Code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NationalBiologyActs",
                schema: "cat");
        }
    }
}
