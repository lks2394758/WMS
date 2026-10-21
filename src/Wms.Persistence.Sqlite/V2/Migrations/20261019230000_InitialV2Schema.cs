using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wms.Persistence.Sqlite.V2.Migrations;

/// <summary>
/// Creates the complete v2 schema (16 business tables, constraints, indexes, triggers). Hand-written SQL, FROZEN in
/// <see cref="InitialV2SchemaSql"/>: SQLite STRICT tables, partial unique indexes and triggers are not expressible
/// through the model differ. Applied by <c>MigrateAsync</c> and recorded in <c>__EFMigrationsHistory</c> of the v2 file
/// only. Later schema changes are new migrations; this one is never edited.
/// </summary>
[DbContext(typeof(WmsV2DbContext))]
[Migration("20261019230000_InitialV2Schema")]
public sealed class InitialV2Schema : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        foreach (var statement in InitialV2SchemaSql.Up)
        {
            migrationBuilder.Sql(statement);
        }
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        foreach (var statement in InitialV2SchemaSql.Down)
        {
            migrationBuilder.Sql(statement);
        }
    }
}
