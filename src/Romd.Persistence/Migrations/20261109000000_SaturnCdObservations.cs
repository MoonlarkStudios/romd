using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Romd.Persistence.Migrations;

/// <summary>Adds distinct Saturn observations without converting or backfilling retained PS1 evidence.</summary>
[DbContext(typeof(RomdDbContext))]
[Migration("20261109000000_SaturnCdObservations")]
public sealed class SaturnCdObservations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE romd."CdBootInspections"
            DROP CONSTRAINT "CdBootInspections_Profile_check",
            ALTER COLUMN "VolumeBlocks" DROP NOT NULL,
            ALTER COLUMN "RootBlock" DROP NOT NULL,
            ALTER COLUMN "RootBytes" DROP NOT NULL,
            ALTER COLUMN "BootPath" DROP NOT NULL,
            ALTER COLUMN "BootBlock" DROP NOT NULL,
            ALTER COLUMN "BootBytes" DROP NOT NULL,
            ALTER COLUMN "EntryPoint" DROP NOT NULL,
            ALTER COLUMN "LoadAddress" DROP NOT NULL,
            ALTER COLUMN "LoadBytes" DROP NOT NULL,
            ADD COLUMN "SaturnProductCode" varchar(10),
            ADD COLUMN "SaturnAreaSymbols" varchar(16),
            ADD COLUMN "SaturnInitialProgramBytes" bigint,
            ADD COLUMN "SaturnHeaderSha256" bytea,
            ADD CONSTRAINT "CK_CdBootInspections_Family" CHECK (((
                "Profile" = 'ps1-cd-root-exe/v1' AND
                "VolumeBlocks" IS NOT NULL AND "RootBlock" IS NOT NULL AND "RootBytes" IS NOT NULL AND "BootPath" IS NOT NULL AND
                "BootBlock" IS NOT NULL AND "BootBytes" IS NOT NULL AND "EntryPoint" IS NOT NULL AND "LoadAddress" IS NOT NULL AND "LoadBytes" IS NOT NULL AND
                "SaturnProductCode" IS NULL AND "SaturnAreaSymbols" IS NULL AND "SaturnInitialProgramBytes" IS NULL AND "SaturnHeaderSha256" IS NULL
            ) OR (
                "Profile" = 'saturn-cd-ip/v1' AND
                "VolumeBlocks" IS NULL AND "RootBlock" IS NULL AND "RootBytes" IS NULL AND "BootPath" IS NULL AND
                "BootBlock" IS NULL AND "BootBytes" IS NULL AND "EntryPoint" IS NULL AND "LoadAddress" IS NULL AND "LoadBytes" IS NULL AND
                "SaturnProductCode" IS NOT NULL AND "SaturnProductCode" ~ '^[!-~]{1,10}$' AND
                "SaturnAreaSymbols" IS NOT NULL AND "SaturnAreaSymbols" ~ '^[JTUBKAEL ]{1,16}$' AND btrim("SaturnAreaSymbols") <> '' AND
                "SaturnInitialProgramBytes" IS NOT NULL AND "SaturnInitialProgramBytes" BETWEEN 256 AND 32768 AND
                "SaturnHeaderSha256" IS NOT NULL AND octet_length("SaturnHeaderSha256") = 32 AND "SaturnHeaderSha256" <> decode(repeat('00',32),'hex')
            )) IS TRUE);

        CREATE OR REPLACE FUNCTION romd.guard_cd_boot_inspection() RETURNS trigger LANGUAGE plpgsql SET search_path = '' AS $body$
        DECLARE first_kind text; first_frames bigint; total_frames bigint;
        BEGIN
            IF TG_OP = 'UPDATE' THEN RAISE EXCEPTION 'CD boot observations are immutable' USING ERRCODE = '23514';
            ELSIF TG_OP = 'DELETE' THEN
                IF EXISTS (SELECT 1 FROM romd."Representations" WHERE "Id" = OLD."RepresentationId") THEN
                    RAISE EXCEPTION 'CD boot observations remain for their representation lifetime' USING ERRCODE = '23514';
                END IF;
                RETURN OLD;
            END IF;
            IF current_setting('transaction_isolation') <> 'read committed' THEN
                RAISE EXCEPTION 'CD boot publication requires ReadCommitted authority' USING ERRCODE = '23514';
            END IF;
            PERFORM 1 FROM romd."Roms" WHERE "Id" IN (SELECT "RomId" FROM romd."RepresentationOutputs" WHERE "RepresentationId" = NEW."RepresentationId")
                ORDER BY "Id" FOR NO KEY UPDATE;
            PERFORM pg_advisory_xact_lock(9415,NEW."RepresentationId");
            PERFORM 1 FROM romd."Representations" WHERE "Id" = NEW."RepresentationId" FOR NO KEY UPDATE;
            SELECT "Kind","Frames" INTO first_kind,first_frames FROM romd."ReconstructionTracks" WHERE "RepresentationId" = NEW."RepresentationId" AND "Number" = 1;
            SELECT sum("Frames") INTO total_frames FROM romd."ReconstructionTracks" WHERE "RepresentationId" = NEW."RepresentationId";
            IF first_kind IS NULL OR first_kind = 'Audio' OR NOT EXISTS (
                SELECT 1 FROM romd."Verifications" v JOIN romd."ChdContainerParameters" c ON c."RepresentationId" = v."RepresentationId"
                WHERE v."Id" = NEW."VerificationId" AND v."RepresentationId" = NEW."RepresentationId" AND v."DecoderRid" IS NOT NULL AND c."UnitBytes" = 2448
            ) OR EXISTS (SELECT 1 FROM romd."CdBootInspections" b WHERE b."RepresentationId" = NEW."RepresentationId" AND b."Profile" <> NEW."Profile") OR (
                NEW."Profile" = 'ps1-cd-root-exe/v1' AND (
                    NEW."FirstTrackIndex01" + NEW."VolumeBlocks" > total_frames OR
                    NEW."FirstTrackIndex01" + NEW."RootBlock" + (NEW."RootBytes" + 2047) / 2048 > first_frames OR
                    NEW."FirstTrackIndex01" + NEW."BootBlock" + (NEW."BootBytes" + 2047) / 2048 > first_frames)
            ) OR (NEW."Profile" = 'saturn-cd-ip/v1' AND (first_kind <> 'Mode1Raw' OR
                NEW."FirstTrackIndex01" + (NEW."SaturnInitialProgramBytes" + 2047) / 2048 > first_frames)) THEN
                RAISE EXCEPTION 'CD boot geometry requires its exact complete measured first data track' USING ERRCODE = '23514';
            END IF;
            RETURN NEW;
        END $body$;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Retained Saturn observations cannot be discarded by a downgrade.");
}
