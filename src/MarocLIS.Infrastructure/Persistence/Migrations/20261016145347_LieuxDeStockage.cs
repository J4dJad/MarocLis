using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarocLIS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LieuxDeStockage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StorageLocations",
                schema: "smp",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StorageLocations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "UX_StorageLocations_Site_Name",
                schema: "smp",
                table: "StorageLocations",
                columns: new[] { "SiteId", "Name" },
                unique: true);

            // The list starts from the places already written on the tubes, per site, trimmed, one
            // entry per name (case ignored by the collation), so nothing typed so far is lost. The
            // tubes themselves are not touched: they keep the text they were given.
            migrationBuilder.Sql(@"
INSERT INTO [smp].[StorageLocations] ([Id], [SiteId], [Name], [IsActive], [CreatedAt])
SELECT NEWID(), p.[SiteId], p.[Name], 1, SYSDATETIMEOFFSET()
FROM (
    SELECT [SiteId], MIN(LTRIM(RTRIM([StorageLocation]))) AS [Name]
    FROM [smp].[Samples]
    WHERE [StorageLocation] IS NOT NULL AND LTRIM(RTRIM([StorageLocation])) <> N''
    GROUP BY [SiteId], LTRIM(RTRIM([StorageLocation]))
) p
WHERE NOT EXISTS (SELECT 1 FROM [smp].[StorageLocations] l
                  WHERE l.[SiteId] = p.[SiteId] AND l.[Name] = p.[Name]);
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StorageLocations",
                schema: "smp");
        }
    }
}
