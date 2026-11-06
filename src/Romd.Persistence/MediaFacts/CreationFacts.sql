-- Encoder provenance is separate from verification. Imports do not invent a creation claim.
CREATE TABLE romd."RepresentationCreations" (
    "Id" uuid PRIMARY KEY CHECK ("Id" <> '00000000-0000-0000-0000-000000000000'),
    "RepresentationId" integer NOT NULL REFERENCES romd."Representations" ("Id") ON DELETE CASCADE,
    "VerificationId" uuid NOT NULL,
    "RecipeId" varchar(128) NOT NULL CHECK (btrim("RecipeId") <> '' AND "RecipeId" !~ '[[:cntrl:]]'),
    "EncoderBuild" varchar(256) NOT NULL CHECK (btrim("EncoderBuild") <> '' AND "EncoderBuild" !~ '[[:cntrl:]]'),
    "ComparedAtUtcTicks" bigint NOT NULL CHECK ("ComparedAtUtcTicks" BETWEEN 0 AND 3155378975999999999),
    "ParametersSha256" bytea NOT NULL CHECK (octet_length("ParametersSha256") = 32 AND "ParametersSha256" <> decode(repeat('00',32),'hex')),
    "EncoderRid" varchar(32),
    "EncoderSourceTag" varchar(32),
    "EncoderSourceSha256" bytea,
    "EncoderRecipeVersion" integer,
    FOREIGN KEY ("VerificationId","RepresentationId") REFERENCES romd."Verifications" ("Id","RepresentationId") ON DELETE CASCADE,
    CHECK (("EncoderRid" IS NULL AND "EncoderSourceTag" IS NULL AND "EncoderSourceSha256" IS NULL AND "EncoderRecipeVersion" IS NULL) OR
        ("EncoderRid" IS NOT NULL AND "EncoderSourceTag" IS NOT NULL AND "EncoderSourceSha256" IS NOT NULL AND "EncoderRecipeVersion" IS NOT NULL AND
         "EncoderRid" ~ '^[a-z0-9.-]{1,32}$' AND "EncoderSourceTag" ~ '^[a-z0-9.-]{1,32}$' AND
         octet_length("EncoderSourceSha256") = 32 AND "EncoderSourceSha256" <> decode(repeat('00',32),'hex') AND "EncoderRecipeVersion" > 0))
);
-- Source keys are historical witnesses, checked while both graphs are retained. Releasing the source does not delete provenance.
-- Its logical identity remains the exact immutable identity of the target output it was compared with.
CREATE TABLE romd."CreationSources" (
    "CreationId" uuid NOT NULL REFERENCES romd."RepresentationCreations" ("Id") ON DELETE CASCADE,
    "TargetOutputId" integer NOT NULL REFERENCES romd."RepresentationOutputs" ("Id") ON DELETE CASCADE,
    "SourceRepresentationId" integer NOT NULL CHECK ("SourceRepresentationId" > 0),
    "SourceOutputId" integer NOT NULL CHECK ("SourceOutputId" > 0),
    "SourceVerificationId" uuid NOT NULL CHECK ("SourceVerificationId" <> '00000000-0000-0000-0000-000000000000'),
    "SourceFileId" integer NOT NULL CHECK ("SourceFileId" > 0),
    "SourceArtifactSha256" bytea NOT NULL CHECK (octet_length("SourceArtifactSha256") = 32 AND "SourceArtifactSha256" <> decode(repeat('00',32),'hex')),
    "SourceArtifactSize" bigint NOT NULL CHECK ("SourceArtifactSize" >= 0),
    PRIMARY KEY ("CreationId","TargetOutputId")
);
CREATE FUNCTION romd.guard_representation_creation() RETURNS trigger LANGUAGE plpgsql SET search_path = '' AS $body$
DECLARE encoding text;
BEGIN
    IF TG_OP = 'UPDATE' THEN RAISE EXCEPTION 'Creation observations are immutable' USING ERRCODE = '23514';
    ELSIF TG_OP = 'DELETE' THEN
        IF EXISTS (SELECT 1 FROM romd."Representations" WHERE "Id" = OLD."RepresentationId") THEN
            RAISE EXCEPTION 'Creation observations remain for their representation lifetime' USING ERRCODE = '23514';
        END IF;
        RETURN OLD;
    END IF;
    IF current_setting('transaction_isolation') <> 'read committed' THEN
        RAISE EXCEPTION 'Creation publication requires ReadCommitted authority' USING ERRCODE = '23514';
    END IF;
    PERFORM 1 FROM romd."Roms" WHERE "Id" IN (SELECT "RomId" FROM romd."RepresentationOutputs" WHERE "RepresentationId" = NEW."RepresentationId")
        ORDER BY "Id" FOR NO KEY UPDATE;
    PERFORM pg_advisory_xact_lock(9415,NEW."RepresentationId");
    SELECT "Encoding" INTO encoding FROM romd."Representations" WHERE "Id" = NEW."RepresentationId" FOR NO KEY UPDATE;
    IF NOT FOUND OR encoding = 'Identity' OR (encoding = 'Chd') IS DISTINCT FROM (NEW."EncoderRid" IS NOT NULL) OR
        NOT EXISTS (SELECT 1 FROM romd."Verifications" WHERE "Id" = NEW."VerificationId" AND "RepresentationId" = NEW."RepresentationId" AND
            "ObservedParametersSha256" = NEW."ParametersSha256") THEN
        RAISE EXCEPTION 'Creation requires exact encoded measurement, parameters and encoder identity' USING ERRCODE = '23514';
    END IF;
    IF NEW."EncoderRid" IS NOT NULL AND NEW."EncoderBuild" <> 'chdman/' || NEW."EncoderRid" || '/' || NEW."EncoderSourceTag" ||
        '/source:' || encode(NEW."EncoderSourceSha256",'hex') || '/recipe:' || NEW."EncoderRecipeVersion"::text THEN
        RAISE EXCEPTION 'Native encoder display label contradicts its identity' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END $body$;
CREATE TRIGGER "TR_RepresentationCreations_Immutable" BEFORE INSERT OR UPDATE OR DELETE ON romd."RepresentationCreations"
    FOR EACH ROW EXECUTE FUNCTION romd.guard_representation_creation();

CREATE FUNCTION romd.guard_creation_source() RETURNS trigger LANGUAGE plpgsql SET search_path = '' AS $body$
DECLARE target_representation integer; target_verification uuid;
BEGIN
    IF TG_OP = 'UPDATE' OR TG_OP = 'DELETE' AND EXISTS (SELECT 1 FROM romd."RepresentationCreations" WHERE "Id" = OLD."CreationId") THEN
        RAISE EXCEPTION 'Retained creation sources are immutable' USING ERRCODE = '23514';
    END IF;
    IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
    SELECT "RepresentationId","VerificationId" INTO target_representation,target_verification FROM romd."RepresentationCreations" WHERE "Id" = NEW."CreationId";
    IF NOT FOUND THEN RAISE EXCEPTION 'Creation requires a retained target measurement' USING ERRCODE = '23514'; END IF;
    -- A fresh invocation can reproduce identical bytes and therefore reuse the same retained decoder proof.
    -- Only the same complete graph may share that proof, with a genuinely later comparison observation.
    IF target_verification = NEW."SourceVerificationId" AND (target_representation <> NEW."SourceRepresentationId" OR NOT EXISTS (
        SELECT 1 FROM romd."RepresentationCreations" c JOIN romd."Verifications" v ON v."Id" = c."VerificationId"
        WHERE c."Id" = NEW."CreationId" AND c."ComparedAtUtcTicks" > v."ObservedAtUtcTicks")) THEN
        RAISE EXCEPTION 'Proof reuse requires the same measured graph and a fresh encoding comparison' USING ERRCODE = '23514';
    END IF;
    PERFORM 1 FROM romd."Roms" WHERE "Id" IN (SELECT "RomId" FROM romd."RepresentationOutputs" WHERE "RepresentationId" = NEW."SourceRepresentationId")
        ORDER BY "Id" FOR NO KEY UPDATE;
    PERFORM pg_advisory_xact_lock(9415,NEW."SourceRepresentationId");
    PERFORM 1 FROM romd."Representations" WHERE "Id" = NEW."SourceRepresentationId" FOR NO KEY UPDATE;
    IF NOT EXISTS (
        SELECT 1 FROM romd."VerificationOutputs" s JOIN romd."Verifications" v ON v."Id" = s."VerificationId" AND v."RepresentationId" = s."RepresentationId"
        JOIN romd."Representations" r ON r."Id" = s."RepresentationId"
        JOIN romd."RepresentationOutputs" so ON so."Id" = s."OutputId" AND so."RepresentationId" = r."Id"
        JOIN romd."RepresentationOutputs" o ON o."Id" = NEW."TargetOutputId" AND o."RepresentationId" = target_representation
        JOIN romd."VerificationOutputs" t ON t."OutputId" = o."Id" AND t."VerificationId" = target_verification AND t."RepresentationId" = o."RepresentationId"
        WHERE s."VerificationId" = NEW."SourceVerificationId" AND s."OutputId" = NEW."SourceOutputId" AND r."Id" = NEW."SourceRepresentationId" AND
            r."FileId" = NEW."SourceFileId" AND v."ArtifactSha256" = NEW."SourceArtifactSha256" AND v."ArtifactSize" = NEW."SourceArtifactSize" AND
            so."RomId" IS NOT DISTINCT FROM o."RomId" AND
            (s."ContentSha256",s."ContentSize",s."Sha1",s."Md5",s."Crc32") = (t."ContentSha256",t."ContentSize",t."Sha1",t."Md5",t."Crc32")
    ) THEN RAISE EXCEPTION 'Creation source requires exact independently measured equivalent output' USING ERRCODE = '23514'; END IF;
    RETURN NEW;
END $body$;
CREATE TRIGGER "TR_CreationSources_Immutable" BEFORE INSERT OR UPDATE OR DELETE ON romd."CreationSources"
    FOR EACH ROW EXECUTE FUNCTION romd.guard_creation_source();

CREATE FUNCTION romd.check_creation_complete() RETURNS trigger LANGUAGE plpgsql SET search_path = '' AS $body$
DECLARE creation_id uuid;
BEGIN
    IF TG_TABLE_NAME = 'RepresentationCreations' THEN creation_id := CASE WHEN TG_OP = 'DELETE' THEN OLD."Id" ELSE NEW."Id" END;
    ELSE creation_id := CASE WHEN TG_OP = 'DELETE' THEN OLD."CreationId" ELSE NEW."CreationId" END; END IF;
    IF EXISTS (SELECT 1 FROM romd."RepresentationCreations" c JOIN romd."RepresentationOutputs" o ON o."RepresentationId" = c."RepresentationId"
        WHERE c."Id" = creation_id AND NOT EXISTS (SELECT 1 FROM romd."CreationSources" s WHERE s."CreationId" = c."Id" AND s."TargetOutputId" = o."Id")) THEN
        RAISE EXCEPTION 'Creation requires an independently compared source for every output' USING ERRCODE = '23514';
    END IF;
    RETURN NULL;
END $body$;
CREATE CONSTRAINT TRIGGER "TR_RepresentationCreations_Complete" AFTER INSERT OR UPDATE OR DELETE ON romd."RepresentationCreations"
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION romd.check_creation_complete();
CREATE CONSTRAINT TRIGGER "TR_CreationSources_Complete" AFTER INSERT OR UPDATE OR DELETE ON romd."CreationSources"
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION romd.check_creation_complete();

DO $body$
DECLARE runtime text;
BEGIN
    FOREACH runtime IN ARRAY ARRAY['romd_admin','romd_consumer'] LOOP
        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = runtime) THEN
            EXECUTE format('REVOKE INSERT,UPDATE,DELETE,TRUNCATE ON romd."RepresentationCreations",romd."CreationSources" FROM %I',runtime);
        END IF;
    END LOOP;
END $body$;
