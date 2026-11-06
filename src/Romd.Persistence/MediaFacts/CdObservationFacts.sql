-- Raw CD boot inspection is independent of encoding: verification/import never fabricates encoder provenance.
CREATE TABLE romd."CdBootInspections" (
    "Id" uuid PRIMARY KEY CHECK ("Id" <> '00000000-0000-0000-0000-000000000000'),
    "RepresentationId" integer NOT NULL REFERENCES romd."Representations" ("Id") ON DELETE CASCADE,
    "VerificationId" uuid NOT NULL,
    "InspectorBuild" varchar(256) NOT NULL CHECK (btrim("InspectorBuild") <> '' AND "InspectorBuild" !~ '[[:cntrl:]]'),
    "Profile" varchar(128) NOT NULL CHECK ("Profile" = 'ps1-cd-root-exe/v1'),
    "ObservedAtUtcTicks" bigint NOT NULL CHECK ("ObservedAtUtcTicks" BETWEEN 0 AND 3155378975999999999),
    "FirstTrackIndex01" bigint NOT NULL CHECK ("FirstTrackIndex01" BETWEEN 0 AND 450000),
    "VolumeBlocks" bigint NOT NULL CHECK ("VolumeBlocks" BETWEEN 17 AND 450000),
    "RootBlock" bigint NOT NULL,
    "RootBytes" bigint NOT NULL CHECK ("RootBytes" BETWEEN 1 AND 262144),
    "BootPath" varchar(66) NOT NULL CHECK (length("BootPath") > 0 AND "BootPath" !~ '[[:cntrl:]/]' AND position(chr(92) IN "BootPath") = 0),
    "BootBlock" bigint NOT NULL,
    "BootBytes" bigint NOT NULL CHECK ("BootBytes" BETWEEN 2048 AND 4294967295),
    "EntryPoint" bigint NOT NULL CHECK ("EntryPoint" BETWEEN 1 AND 4294967295 AND "EntryPoint" % 4 = 0),
    "LoadAddress" bigint NOT NULL CHECK ("LoadAddress" BETWEEN 1 AND 4294967295),
    "LoadBytes" bigint NOT NULL CHECK ("LoadBytes" BETWEEN 1 AND 2097152),
    UNIQUE ("Id","RepresentationId"),
    UNIQUE ("RepresentationId","InspectorBuild","Profile"),
    FOREIGN KEY ("VerificationId","RepresentationId") REFERENCES romd."Verifications" ("Id","RepresentationId") ON DELETE CASCADE,
    CHECK ("RootBlock" BETWEEN 17 AND "VolumeBlocks" - 1 AND "BootBlock" BETWEEN 17 AND "VolumeBlocks" - 1 AND
        "RootBlock" * 2048 + "RootBytes" <= "VolumeBlocks" * 2048 AND "BootBlock" * 2048 + "BootBytes" <= "VolumeBlocks" * 2048 AND
        "BootBytes" >= 2048 + "LoadBytes" AND ("EntryPoint" >> 24) IN (128,160) AND ("EntryPoint" & 16777215) + 4 <= 2097152 AND
        ("LoadAddress" >> 24) IN (128,160) AND ("LoadAddress" & 16777215) + "LoadBytes" <= 2097152)
);
CREATE TABLE romd."CdEncodingObservations" (
    "CreationId" uuid PRIMARY KEY REFERENCES romd."RepresentationCreations" ("Id") ON DELETE CASCADE,
    "RepresentationId" integer NOT NULL,
    "BootInspectionId" uuid NOT NULL,
    "SourceRequestSha256" bytea NOT NULL CHECK (octet_length("SourceRequestSha256") = 32 AND "SourceRequestSha256" <> decode(repeat('00',32),'hex')),
    "SourceRequestBytes" bigint NOT NULL CHECK ("SourceRequestBytes" BETWEEN 1 AND 524288),
    FOREIGN KEY ("BootInspectionId","RepresentationId") REFERENCES romd."CdBootInspections" ("Id","RepresentationId") ON DELETE CASCADE
);

CREATE FUNCTION romd.guard_cd_boot_inspection() RETURNS trigger LANGUAGE plpgsql SET search_path = '' AS $body$
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
    ) OR NEW."FirstTrackIndex01" + NEW."VolumeBlocks" > total_frames OR
        NEW."FirstTrackIndex01" + NEW."RootBlock" + (NEW."RootBytes" + 2047) / 2048 > first_frames OR
        NEW."FirstTrackIndex01" + NEW."BootBlock" + (NEW."BootBytes" + 2047) / 2048 > first_frames THEN
        RAISE EXCEPTION 'CD boot geometry requires its exact complete measured first data track' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END $body$;
CREATE TRIGGER "TR_CdBootInspections_Immutable" BEFORE INSERT OR UPDATE OR DELETE ON romd."CdBootInspections"
    FOR EACH ROW EXECUTE FUNCTION romd.guard_cd_boot_inspection();

CREATE FUNCTION romd.guard_cd_encoding_observation() RETURNS trigger LANGUAGE plpgsql SET search_path = '' AS $body$
BEGIN
    IF TG_OP = 'UPDATE' OR TG_OP = 'DELETE' AND EXISTS (SELECT 1 FROM romd."RepresentationCreations" WHERE "Id" = OLD."CreationId") THEN
        RAISE EXCEPTION 'CD encoding observations are immutable for their representation lifetime' USING ERRCODE = '23514';
    END IF;
    IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
    IF NOT EXISTS (
        SELECT 1 FROM romd."RepresentationCreations" c JOIN romd."ReconstructionManifests" m ON m."RepresentationId" = c."RepresentationId"
        JOIN romd."CdBootInspections" b ON b."Id" = NEW."BootInspectionId" AND b."RepresentationId" = c."RepresentationId"
        WHERE c."Id" = NEW."CreationId" AND c."RepresentationId" = NEW."RepresentationId" AND c."EncoderRid" IS NOT NULL
    ) THEN RAISE EXCEPTION 'CD encoder/request observation requires its exact creation and measured boot inspection' USING ERRCODE = '23514'; END IF;
    RETURN NEW;
END $body$;
CREATE TRIGGER "TR_CdEncodingObservations_Immutable" BEFORE INSERT OR UPDATE OR DELETE ON romd."CdEncodingObservations"
    FOR EACH ROW EXECUTE FUNCTION romd.guard_cd_encoding_observation();

CREATE FUNCTION romd.check_cd_creation_complete() RETURNS trigger LANGUAGE plpgsql SET search_path = '' AS $body$
DECLARE creation_id uuid;
BEGIN
    IF TG_TABLE_NAME = 'RepresentationCreations' THEN creation_id := CASE WHEN TG_OP = 'DELETE' THEN OLD."Id" ELSE NEW."Id" END;
    ELSE creation_id := CASE WHEN TG_OP = 'DELETE' THEN OLD."CreationId" ELSE NEW."CreationId" END; END IF;
    IF EXISTS (SELECT 1 FROM romd."RepresentationCreations" c WHERE c."Id" = creation_id AND
        EXISTS (SELECT 1 FROM romd."ReconstructionManifests" WHERE "RepresentationId" = c."RepresentationId") IS DISTINCT FROM
        EXISTS (SELECT 1 FROM romd."CdEncodingObservations" WHERE "CreationId" = c."Id")) THEN
        RAISE EXCEPTION 'CD creation and exact encoder/request/boot observations publish atomically' USING ERRCODE = '23514';
    END IF;
    RETURN NULL;
END $body$;
CREATE CONSTRAINT TRIGGER "TR_RepresentationCreations_CdComplete" AFTER INSERT OR UPDATE OR DELETE ON romd."RepresentationCreations"
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION romd.check_cd_creation_complete();
CREATE CONSTRAINT TRIGGER "TR_CdEncodingObservations_Complete" AFTER INSERT OR UPDATE OR DELETE ON romd."CdEncodingObservations"
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION romd.check_cd_creation_complete();

DO $body$
DECLARE runtime text;
BEGIN
    FOREACH runtime IN ARRAY ARRAY['romd_admin','romd_consumer'] LOOP
        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = runtime) THEN
            EXECUTE format('REVOKE INSERT,UPDATE,DELETE,TRUNCATE ON romd."CdBootInspections",romd."CdEncodingObservations" FROM %I',runtime);
        END IF;
    END LOOP;
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'romd_worker') THEN
        REVOKE TRUNCATE ON romd."CdBootInspections",romd."CdEncodingObservations",romd."RepresentationCreations",romd."CreationSources",
            romd."LogicalInspections",romd."Ps2BootObservations" FROM romd_worker;
    END IF;
END $body$;
