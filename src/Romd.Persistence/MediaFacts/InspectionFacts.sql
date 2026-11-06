-- Physical logical-byte observations. No media kind, supported flag or reference-derived eligibility is stored.
CREATE TABLE romd."LogicalInspections" (
    "Id" uuid PRIMARY KEY CHECK ("Id" <> '00000000-0000-0000-0000-000000000000'),
    "RomId" integer NOT NULL REFERENCES romd."Roms" ("Id") ON DELETE CASCADE,
    "SourceOutputId" integer NOT NULL CHECK ("SourceOutputId" > 0),
    "SourceVerificationId" uuid NOT NULL CHECK ("SourceVerificationId" <> '00000000-0000-0000-0000-000000000000'),
    "Profile" varchar(128) NOT NULL CHECK (btrim("Profile") <> '' AND "Profile" !~ '[[:cntrl:]]'),
    "InspectorBuild" varchar(256) NOT NULL CHECK (btrim("InspectorBuild") <> '' AND "InspectorBuild" !~ '[[:cntrl:]]'),
    "ObservedAtUtcTicks" bigint NOT NULL CHECK ("ObservedAtUtcTicks" BETWEEN 0 AND 3155378975999999999),
    "Prefix" bytea NOT NULL CHECK (octet_length("Prefix") BETWEEN 0 AND 32),
    "NintendoPartitions" integer,
    "StandardKeyPartitions" integer,
    "KoreanKeyPartitions" integer,
    "HasPs2Boot" boolean NOT NULL,
    UNIQUE ("RomId","Profile","InspectorBuild"),
    CHECK (("NintendoPartitions" IS NULL AND "StandardKeyPartitions" IS NULL AND "KoreanKeyPartitions" IS NULL) OR
        ("NintendoPartitions" IS NOT NULL AND "StandardKeyPartitions" IS NOT NULL AND "KoreanKeyPartitions" IS NOT NULL AND
         "NintendoPartitions" BETWEEN 0 AND 64 AND "StandardKeyPartitions" >= 0 AND "KoreanKeyPartitions" >= 0 AND
         "StandardKeyPartitions"::bigint + "KoreanKeyPartitions" = "NintendoPartitions" AND NOT "HasPs2Boot"))
);
CREATE TABLE romd."Ps2BootObservations" (
    "InspectionId" uuid PRIMARY KEY REFERENCES romd."LogicalInspections" ("Id") ON DELETE CASCADE,
    "LogicalBytes" bigint NOT NULL CHECK ("LogicalBytes" BETWEEN 2048 AND 10737418240 AND "LogicalBytes" % 2048 = 0),
    "VolumeBlocks" bigint NOT NULL,
    "PrimaryDescriptorBlock" bigint NOT NULL,
    "RootDirectoryBlock" bigint NOT NULL,
    "RootDirectoryBytes" bigint NOT NULL,
    "SystemCnfBlock" bigint NOT NULL,
    "SystemCnfBytes" bigint NOT NULL,
    "BootExecutableBlock" bigint NOT NULL,
    "BootExecutableBytes" bigint NOT NULL,
    "ObservedBoot2Value" varchar(21) NOT NULL,
    "BootPath" varchar(21) NOT NULL,
    "BootSerial" varchar(11) NOT NULL,
    "EntryPoint" bigint NOT NULL,
    "ProgramHeaderCount" integer NOT NULL,
    "Profile" varchar(128) NOT NULL,
    "InspectorBuild" varchar(256) NOT NULL,
    CHECK ("PrimaryDescriptorBlock" BETWEEN 16 AND 79 AND "VolumeBlocks" BETWEEN "PrimaryDescriptorBlock" + 2 AND "LogicalBytes" / 2048 AND "RootDirectoryBytes" BETWEEN 1 AND 262144 AND "SystemCnfBytes" BETWEEN 1 AND 16384 AND "BootExecutableBytes" BETWEEN 84 AND 4294967295 AND "RootDirectoryBlock" > "PrimaryDescriptorBlock" + 1 AND "SystemCnfBlock" > "PrimaryDescriptorBlock" + 1 AND "BootExecutableBlock" > "PrimaryDescriptorBlock" + 1 AND "RootDirectoryBlock"::numeric * 2048 + "RootDirectoryBytes" <= "VolumeBlocks"::numeric * 2048 AND "SystemCnfBlock"::numeric * 2048 + "SystemCnfBytes" <= "VolumeBlocks"::numeric * 2048 AND "BootExecutableBlock"::numeric * 2048 + "BootExecutableBytes" <= "VolumeBlocks"::numeric * 2048 AND ("RootDirectoryBlock"::numeric * 2048 + "RootDirectoryBytes" <= "SystemCnfBlock"::numeric * 2048 OR "SystemCnfBlock"::numeric * 2048 + "SystemCnfBytes" <= "RootDirectoryBlock"::numeric * 2048) AND ("RootDirectoryBlock"::numeric * 2048 + "RootDirectoryBytes" <= "BootExecutableBlock"::numeric * 2048 OR "BootExecutableBlock"::numeric * 2048 + "BootExecutableBytes" <= "RootDirectoryBlock"::numeric * 2048) AND ("SystemCnfBlock"::numeric * 2048 + "SystemCnfBytes" <= "BootExecutableBlock"::numeric * 2048 OR "BootExecutableBlock"::numeric * 2048 + "BootExecutableBytes" <= "SystemCnfBlock"::numeric * 2048) AND "EntryPoint" BETWEEN 1 AND 4294967295 AND "EntryPoint" % 4 = 0 AND "ProgramHeaderCount" BETWEEN 1 AND 128 AND 52 + "ProgramHeaderCount" * 32 <= "BootExecutableBytes" AND "BootSerial" ~ '^[A-Z]{4}_[0-9]{3}[.][0-9]{2}$' AND "BootPath" = 'cdrom0:' || chr(92) || "BootSerial" || ';1' AND lower("ObservedBoot2Value") = lower("BootPath") AND "Profile" = 'ps2-iso-root-elf/v1' AND btrim("InspectorBuild") <> '' AND "InspectorBuild" !~ '[[:cntrl:]]')
);

CREATE FUNCTION romd.guard_logical_inspection() RETURNS trigger LANGUAGE plpgsql SET search_path = '' AS $body$
DECLARE representation_id integer; logical_bytes bigint;
BEGIN
    IF TG_OP = 'UPDATE' THEN
        RAISE EXCEPTION 'Logical observations are immutable' USING ERRCODE = '23514';
    ELSIF TG_OP = 'DELETE' THEN
        IF EXISTS (SELECT 1 FROM romd."Roms" WHERE "Id" = OLD."RomId") THEN
            RAISE EXCEPTION 'Logical observations remain until their logical ROM is removed' USING ERRCODE = '23514';
        END IF;
        RETURN OLD;
    END IF;
    IF current_setting('transaction_isolation') <> 'read committed' THEN
        RAISE EXCEPTION 'Logical inspection publication requires ReadCommitted authority' USING ERRCODE = '23514';
    END IF;
    SELECT "ContentSize" INTO logical_bytes FROM romd."Roms" WHERE "Id" = NEW."RomId" FOR NO KEY UPDATE;
    IF NOT FOUND OR octet_length(NEW."Prefix") <> least(logical_bytes,32) OR
        NEW."NintendoPartitions" IS NOT NULL AND logical_bytes < 32 THEN
        RAISE EXCEPTION 'Inspection prefix must describe its exact logical ROM' USING ERRCODE = '23514';
    END IF;
    IF EXISTS (SELECT 1 FROM romd."LogicalInspections" WHERE "RomId" = NEW."RomId" AND "Prefix" <> NEW."Prefix") THEN
        RAISE EXCEPTION 'Measured prefixes of the same logical bytes must agree' USING ERRCODE = '23514';
    END IF;
    SELECT "RepresentationId" INTO representation_id FROM romd."RepresentationOutputs" WHERE "Id" = NEW."SourceOutputId";
    IF NOT FOUND THEN RAISE EXCEPTION 'Inspection source output is missing' USING ERRCODE = '23514'; END IF;
    PERFORM pg_advisory_xact_lock(9415,representation_id);
    PERFORM 1 FROM romd."Representations" WHERE "Id" = representation_id FOR NO KEY UPDATE;
    IF NOT EXISTS (
        SELECT 1 FROM romd."VerificationOutputs" v JOIN romd."RepresentationOutputs" o ON o."Id" = v."OutputId" AND o."RepresentationId" = v."RepresentationId"
        JOIN romd."Roms" r ON r."Id" = o."RomId"
        WHERE v."VerificationId" = NEW."SourceVerificationId" AND o."Id" = NEW."SourceOutputId" AND r."Id" = NEW."RomId" AND
            (v."ContentSha256",v."ContentSize",v."Sha1",v."Md5",v."Crc32") = (r."ContentSha256",r."ContentSize",r."Sha1",r."Md5",r."Crc32")
    ) THEN RAISE EXCEPTION 'Inspection requires an exact complete measured owned output' USING ERRCODE = '23514'; END IF;
    RETURN NEW;
END $body$;
CREATE TRIGGER "TR_LogicalInspections_Immutable" BEFORE INSERT OR UPDATE OR DELETE ON romd."LogicalInspections"
    FOR EACH ROW EXECUTE FUNCTION romd.guard_logical_inspection();

CREATE FUNCTION romd.guard_ps2_boot_observation() RETURNS trigger LANGUAGE plpgsql SET search_path = '' AS $body$
BEGIN
    IF TG_OP = 'UPDATE' OR TG_OP = 'DELETE' AND EXISTS (SELECT 1 FROM romd."LogicalInspections" WHERE "Id" = OLD."InspectionId") THEN
        RAISE EXCEPTION 'Retained boot observations are immutable' USING ERRCODE = '23514';
    END IF;
    IF TG_OP = 'INSERT' AND NOT EXISTS (
        SELECT 1 FROM romd."LogicalInspections" i JOIN romd."Roms" r ON r."Id" = i."RomId"
        WHERE i."Id" = NEW."InspectionId" AND i."HasPs2Boot" AND r."ContentSize" = NEW."LogicalBytes"
    ) THEN RAISE EXCEPTION 'Boot geometry requires its exact logical inspection' USING ERRCODE = '23514'; END IF;
    IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
    RETURN NEW;
END $body$;
CREATE TRIGGER "TR_Ps2BootObservations_Immutable" BEFORE INSERT OR UPDATE OR DELETE ON romd."Ps2BootObservations"
    FOR EACH ROW EXECUTE FUNCTION romd.guard_ps2_boot_observation();

CREATE FUNCTION romd.check_logical_inspection() RETURNS trigger LANGUAGE plpgsql SET search_path = '' AS $body$
DECLARE inspection_id uuid;
BEGIN
    IF TG_TABLE_NAME = 'LogicalInspections' THEN
        inspection_id := CASE WHEN TG_OP = 'DELETE' THEN OLD."Id" ELSE NEW."Id" END;
    ELSE
        inspection_id := CASE WHEN TG_OP = 'DELETE' THEN OLD."InspectionId" ELSE NEW."InspectionId" END;
    END IF;
    IF EXISTS (SELECT 1 FROM romd."LogicalInspections" i LEFT JOIN romd."Ps2BootObservations" b ON b."InspectionId" = i."Id"
        WHERE i."Id" = inspection_id AND i."HasPs2Boot" IS DISTINCT FROM (b."InspectionId" IS NOT NULL)) THEN
        RAISE EXCEPTION 'Inspection and boot observations publish atomically' USING ERRCODE = '23514';
    END IF;
    RETURN NULL;
END $body$;
CREATE CONSTRAINT TRIGGER "TR_LogicalInspections_Complete" AFTER INSERT OR UPDATE OR DELETE ON romd."LogicalInspections"
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION romd.check_logical_inspection();
CREATE CONSTRAINT TRIGGER "TR_Ps2BootObservations_Complete" AFTER INSERT OR UPDATE OR DELETE ON romd."Ps2BootObservations"
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION romd.check_logical_inspection();

CREATE FUNCTION romd.guard_inspected_logical_identity() RETURNS trigger LANGUAGE plpgsql SET search_path = '' AS $body$
BEGIN
    IF (NEW."ContentSha256",NEW."ContentSize",NEW."Sha1",NEW."Md5",NEW."Crc32") IS DISTINCT FROM
        (OLD."ContentSha256",OLD."ContentSize",OLD."Sha1",OLD."Md5",OLD."Crc32") AND
        EXISTS (SELECT 1 FROM romd."LogicalInspections" WHERE "RomId" = OLD."Id") THEN
        RAISE EXCEPTION 'Inspected logical identities are immutable' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END $body$;
CREATE TRIGGER "TR_Roms_InspectedIdentity" BEFORE UPDATE ON romd."Roms" FOR EACH ROW EXECUTE FUNCTION romd.guard_inspected_logical_identity();

DO $body$
DECLARE runtime text;
BEGIN
    FOREACH runtime IN ARRAY ARRAY['romd_admin','romd_consumer'] LOOP
        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = runtime) THEN
            EXECUTE format('REVOKE INSERT,UPDATE,DELETE,TRUNCATE ON romd."LogicalInspections",romd."Ps2BootObservations" FROM %I',runtime);
        END IF;
    END LOOP;
END $body$;
