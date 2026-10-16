using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarocLIS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CompteurDossiersCommun : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // File numbers (Purpose 0) and subcontracting slips (Purpose 12) now come from one
            // counter for all sites. The shared counter continues after the highest number any
            // site has issued, keeps the format of the busiest site, and the per-site counters go.
            migrationBuilder.Sql(@"
;WITH per AS (
    SELECT [Purpose], [Year], MAX([NextValue]) AS NextValue, MAX([LastAllocatedValue]) AS LastAllocatedValue,
           MAX([LastAllocatedAt]) AS LastAllocatedAt
    FROM [cfg].[NumberSequences] WHERE [Purpose] IN (0, 12) AND [SiteId] IS NOT NULL
    GROUP BY [Purpose], [Year])
UPDATE n SET
    [NextValue] = CASE WHEN p.NextValue > n.[NextValue] THEN p.NextValue ELSE n.[NextValue] END,
    [LastAllocatedValue] = CASE WHEN p.LastAllocatedValue > n.[LastAllocatedValue] THEN p.LastAllocatedValue ELSE n.[LastAllocatedValue] END,
    [LastAllocatedAt] = CASE WHEN n.[LastAllocatedAt] IS NULL OR p.LastAllocatedAt > n.[LastAllocatedAt] THEN p.LastAllocatedAt ELSE n.[LastAllocatedAt] END
FROM [cfg].[NumberSequences] n
JOIN per p ON p.[Purpose] = n.[Purpose] AND (p.[Year] = n.[Year] OR (p.[Year] IS NULL AND n.[Year] IS NULL))
WHERE n.[SiteId] IS NULL;

;WITH per AS (
    SELECT [Purpose], [Year], MAX([NextValue]) AS NextValue, MAX([LastAllocatedValue]) AS LastAllocatedValue,
           MAX([LastAllocatedAt]) AS LastAllocatedAt
    FROM [cfg].[NumberSequences] WHERE [Purpose] IN (0, 12) AND [SiteId] IS NOT NULL
    GROUP BY [Purpose], [Year])
INSERT INTO [cfg].[NumberSequences]
    ([Id], [Purpose], [SiteId], [Year], [Format], [NextValue], [LastAllocatedValue], [LastAllocatedAt], [IsClosed], [ClosedAt])
SELECT NEWID(), p.[Purpose], NULL, p.[Year],
       (SELECT TOP 1 x.[Format] FROM [cfg].[NumberSequences] x
        WHERE x.[Purpose] = p.[Purpose] AND x.[SiteId] IS NOT NULL
          AND (x.[Year] = p.[Year] OR (x.[Year] IS NULL AND p.[Year] IS NULL))
        ORDER BY x.[NextValue] DESC),
       p.NextValue, p.LastAllocatedValue, p.LastAllocatedAt, 0, NULL
FROM per p
WHERE NOT EXISTS (SELECT 1 FROM [cfg].[NumberSequences] s
                  WHERE s.[Purpose] = p.[Purpose] AND s.[SiteId] IS NULL
                    AND (s.[Year] = p.[Year] OR (s.[Year] IS NULL AND p.[Year] IS NULL)));

DELETE FROM [cfg].[NumberSequences] WHERE [Purpose] IN (0, 12) AND [SiteId] IS NOT NULL;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
