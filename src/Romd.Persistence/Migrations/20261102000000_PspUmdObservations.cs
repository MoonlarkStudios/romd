using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Romd.Persistence.Migrations;

/// <summary>Additive raw PSP observations; the released fresh baseline and retained observations remain unchanged.</summary>
[DbContext(typeof(RomdDbContext))]
[Migration("20261102000000_PspUmdObservations")]
public sealed class PspUmdObservations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE romd."LogicalInspections"
            ADD COLUMN "PspVolumeBlocks" bigint,
            ADD COLUMN "PspDiscId" varchar(9),
            ADD COLUMN "PspDiscVersion" varchar(4),
            ADD CONSTRAINT "CK_LogicalInspections_Psp" CHECK (((
                "PspVolumeBlocks" IS NULL AND "PspDiscId" IS NULL AND "PspDiscVersion" IS NULL) OR (
                "PspVolumeBlocks" IS NOT NULL AND "PspVolumeBlocks" BETWEEN 18 AND 1048576 AND
                "PspDiscId" IS NOT NULL AND "PspDiscId" ~ '^[A-Z]{4}[0-9]{5}$' AND
                "PspDiscVersion" IS NOT NULL AND "PspDiscVersion" ~ '^[0-9][.][0-9]{2}$' AND
                "NintendoPartitions" IS NULL AND NOT "HasPs2Boot")) IS TRUE);

        CREATE FUNCTION romd.guard_psp_logical_inspection() RETURNS trigger LANGUAGE plpgsql SET search_path = '' AS $body$
        BEGIN
            IF NEW."PspVolumeBlocks" IS NOT NULL AND NOT EXISTS (
                SELECT 1 FROM romd."Roms" r WHERE r."Id" = NEW."RomId" AND
                    r."ContentSize" BETWEEN 36864 AND 2147483648 AND r."ContentSize" % 2048 = 0 AND
                    NEW."PspVolumeBlocks" * 2048 <= r."ContentSize") THEN
                RAISE EXCEPTION 'PSP volume observation requires its exact bounded logical ISO' USING ERRCODE = '23514';
            END IF;
            RETURN NEW;
        END $body$;
        CREATE TRIGGER "TR_LogicalInspections_Psp" BEFORE INSERT ON romd."LogicalInspections"
            FOR EACH ROW EXECUTE FUNCTION romd.guard_psp_logical_inspection();

        -- The historical baseline used an unnamed single-column DiscKind check. Require exactly that check before extending it.
        DO $body$
        DECLARE names text[];
        BEGIN
            SELECT array_agg(c.conname) INTO names FROM pg_constraint c
            JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = c.conkey[1]
            WHERE c.conrelid = 'romd."MediaReconciliationAttempts"'::regclass AND c.contype = 'c' AND
                cardinality(c.conkey) = 1 AND a.attname = 'DiscKind';
            IF cardinality(names) IS DISTINCT FROM 1 THEN
                RAISE EXCEPTION 'Expected one bounded reconciliation disc-kind constraint';
            END IF;
            EXECUTE format('ALTER TABLE romd."MediaReconciliationAttempts" DROP CONSTRAINT %I', names[1]);
        END $body$;
        ALTER TABLE romd."MediaReconciliationAttempts" ADD CONSTRAINT "CK_MediaReconciliationAttempts_DiscKind"
            CHECK (("DiscKind" IS NULL OR "DiscKind" IN ('GameCube','Wii','PlayStation2Dvd','PlayStationPortableUmd')) IS TRUE);
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Retained PSP observations and reconciliation attempts cannot be discarded by a downgrade.");
}
