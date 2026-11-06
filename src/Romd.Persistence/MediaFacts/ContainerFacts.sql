-- Captured numeric container observations. No recipe, canonical flag or kind is stored here.
CREATE TABLE romd."ChdContainerParameters" (
    "RepresentationId" integer PRIMARY KEY REFERENCES romd."Representations" ("Id") ON DELETE CASCADE,
    "Version" integer NOT NULL CHECK ("Version" = 5),
    "LogicalBytes" bigint NOT NULL CHECK ("LogicalBytes" BETWEEN 1 AND 10737418240),
    "HunkBytes" integer NOT NULL CHECK ("HunkBytes" BETWEEN 1 AND 1048576),
    "UnitBytes" integer NOT NULL CHECK ("UnitBytes" BETWEEN 1 AND 2448),
    "HunkCount" bigint NOT NULL CHECK ("HunkCount" BETWEEN 1 AND 4294967295),
    "Codec0" bigint NOT NULL CHECK ("Codec0" BETWEEN 0 AND 4294967295),
    "Codec1" bigint NOT NULL CHECK ("Codec1" BETWEEN 0 AND 4294967295),
    "Codec2" bigint NOT NULL CHECK ("Codec2" BETWEEN 0 AND 4294967295),
    "Codec3" bigint NOT NULL CHECK ("Codec3" BETWEEN 0 AND 4294967295),
    "HasParent" boolean NOT NULL,
    "StoredRawSha1" bytea NOT NULL CHECK (octet_length("StoredRawSha1") = 20 AND "StoredRawSha1" <> decode(repeat('00',20),'hex')),
    "StoredOverallSha1" bytea NOT NULL CHECK (octet_length("StoredOverallSha1") = 20 AND "StoredOverallSha1" <> decode(repeat('00',20),'hex')),
    "MetadataSha256" bytea NOT NULL CHECK (octet_length("MetadataSha256") = 32 AND "MetadataSha256" <> decode(repeat('00',32),'hex')),
    CHECK ("LogicalBytes" % "UnitBytes" = 0 AND "HunkBytes" % "UnitBytes" = 0 AND
        "HunkCount" = "LogicalBytes" / "HunkBytes" + CASE WHEN "LogicalBytes" % "HunkBytes" = 0 THEN 0 ELSE 1 END)
);
CREATE TABLE romd."ChdMetadataEntries" (
    "RepresentationId" integer NOT NULL REFERENCES romd."ChdContainerParameters" ("RepresentationId") ON DELETE CASCADE,
    "Ordinal" integer NOT NULL CHECK ("Ordinal" BETWEEN 0 AND 98),
    "Tag" bigint NOT NULL CHECK ("Tag" BETWEEN 0 AND 4294967295),
    "Flags" integer NOT NULL CHECK ("Flags" BETWEEN 0 AND 255),
    "Payload" bytea NOT NULL CHECK (octet_length("Payload") BETWEEN 1 AND 262144),
    PRIMARY KEY ("RepresentationId", "Ordinal")
);
CREATE TABLE romd."RvzContainerParameters" (
    "RepresentationId" integer PRIMARY KEY REFERENCES romd."Representations" ("Id") ON DELETE CASCADE,
    "Version" bigint NOT NULL CHECK ("Version" BETWEEN 196608 AND 4294967295),
    "DiscType" bigint NOT NULL CHECK ("DiscType" BETWEEN 0 AND 4294967295),
    "Compression" bigint NOT NULL CHECK ("Compression" IN (0,2,3,4,5)),
    "CompressionLevel" integer NOT NULL,
    "BlockBytes" integer NOT NULL CHECK ("BlockBytes" BETWEEN 32768 AND 8388608 AND
        CASE WHEN "BlockBytes" < 2097152 THEN ("BlockBytes" & ("BlockBytes" - 1)) = 0 ELSE "BlockBytes" % 2097152 = 0 END),
    "HasWiiPartitions" boolean NOT NULL,
    "LogicalBytes" bigint NOT NULL CHECK ("LogicalBytes" BETWEEN 1 AND 10737418240)
);
ALTER TABLE romd."Verifications" ADD COLUMN "ComputedRawSha1" bytea, ADD COLUMN "ComputedOverallSha1" bytea,
    ADD COLUMN "ObservedParametersSha256" bytea;

ALTER TABLE romd."Verifications" ADD CONSTRAINT "CK_Verifications_ObservedParameters"
    CHECK ("ObservedParametersSha256" IS NULL OR (octet_length("ObservedParametersSha256") = 32 AND "ObservedParametersSha256" <> decode(repeat('00',32),'hex')));

CREATE FUNCTION romd.media_parameter_fingerprint(representation_id integer) RETURNS bytea
LANGUAGE sql STABLE SET search_path = '' AS $body$
    SELECT CASE r."Encoding"
    WHEN 'Chd' THEN sha256(convert_to('chd-parameters/v1','UTF8') || decode('00','hex') ||
        int4send(c."Version") || int8send(c."LogicalBytes") || int4send(c."HunkBytes") || int4send(c."UnitBytes") || substring(int8send(c."HunkCount") FROM 5) ||
        substring(int8send(c."Codec0") FROM 5) || substring(int8send(c."Codec1") FROM 5) || substring(int8send(c."Codec2") FROM 5) || substring(int8send(c."Codec3") FROM 5) ||
        CASE WHEN c."HasParent" THEN decode('01','hex') ELSE decode('00','hex') END || c."StoredRawSha1" || c."StoredOverallSha1" || c."MetadataSha256")
    WHEN 'Rvz' THEN sha256(convert_to('rvz-parameters/v1','UTF8') || decode('00','hex') ||
        substring(int8send(v."Version") FROM 5) || substring(int8send(v."DiscType") FROM 5) || substring(int8send(v."Compression") FROM 5) || int4send(v."CompressionLevel") ||
        int4send(v."BlockBytes") || CASE WHEN v."HasWiiPartitions" THEN decode('01','hex') ELSE decode('00','hex') END || int8send(v."LogicalBytes"))
    ELSE NULL END
    FROM romd."Representations" r LEFT JOIN romd."ChdContainerParameters" c ON c."RepresentationId" = r."Id"
    LEFT JOIN romd."RvzContainerParameters" v ON v."RepresentationId" = r."Id" WHERE r."Id" = representation_id
$body$;

CREATE FUNCTION romd.validate_media_artifact_agreement(representation_id integer) RETURNS void
LANGUAGE plpgsql SET search_path = '' AS $body$
DECLARE artifact_file integer; encoding text;
BEGIN
    IF NOT EXISTS (SELECT 1 FROM romd."Verifications" WHERE "RepresentationId" = representation_id) THEN RETURN; END IF;
    SELECT "FileId","Encoding" INTO artifact_file,encoding FROM romd."Representations" WHERE "Id" = representation_id;
    IF EXISTS (
        SELECT 1 FROM romd."Representations" sibling JOIN romd."Verifications" measured ON measured."RepresentationId" = sibling."Id"
        WHERE sibling."FileId" = artifact_file AND sibling."Encoding" = encoding AND sibling."Id" <> representation_id AND
            measured."ObservedParametersSha256" IS DISTINCT FROM romd.media_parameter_fingerprint(representation_id)
    ) THEN
        RAISE EXCEPTION 'Shared artifacts require the same independently measured container parameters' USING ERRCODE = '23514';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM romd."ReconstructionManifests" WHERE "RepresentationId" = representation_id) AND EXISTS (
        SELECT 1 FROM romd."Representations" sibling JOIN romd."RepresentationOutputs" other ON other."RepresentationId" = sibling."Id"
        JOIN romd."RepresentationOutputs" own ON own."RepresentationId" = representation_id
        WHERE sibling."FileId" = artifact_file AND sibling."Encoding" = encoding AND sibling."Id" <> representation_id AND
            EXISTS (SELECT 1 FROM romd."Verifications" WHERE "RepresentationId" = sibling."Id") AND
            NOT EXISTS (SELECT 1 FROM romd."ReconstructionManifests" WHERE "RepresentationId" = sibling."Id") AND
            (own."ContentSha256",own."ContentSize",own."Sha1",own."Md5",own."Crc32") IS DISTINCT FROM
            (other."ContentSha256",other."ContentSize",other."Sha1",other."Md5",other."Crc32")
    ) THEN
        RAISE EXCEPTION 'Whole-file measurements of a shared artifact must agree on exact logical bytes' USING ERRCODE = '23514';
    END IF;
END $body$;

CREATE FUNCTION romd.validate_media_container_facts(representation_id integer) RETURNS void
LANGUAGE plpgsql SET search_path = '' AS $body$
DECLARE encoding text; chd romd."ChdContainerParameters"; rvz romd."RvzContainerParameters";
    logical_frames bigint; metadata_hash bytea; metadata_bytes bigint; entries integer; artifact_bytes bigint;
BEGIN
    SELECT "Encoding" INTO encoding FROM romd."Representations" WHERE "Id" = representation_id;
    IF NOT FOUND THEN RETURN; END IF;
    SELECT * INTO chd FROM romd."ChdContainerParameters" WHERE "RepresentationId" = representation_id;
    SELECT * INTO rvz FROM romd."RvzContainerParameters" WHERE "RepresentationId" = representation_id;
    IF (encoding = 'Chd') IS DISTINCT FROM (chd."RepresentationId" IS NOT NULL) OR
        (encoding = 'Rvz') IS DISTINCT FROM (rvz."RepresentationId" IS NOT NULL) THEN
        RAISE EXCEPTION 'A representation requires exactly its captured container parameters' USING ERRCODE = '23514';
    END IF;
    IF encoding = 'Rvz' AND (EXISTS (SELECT 1 FROM romd."ReconstructionManifests" WHERE "RepresentationId" = representation_id) OR
        EXISTS (SELECT 1 FROM romd."RepresentationOutputs" WHERE "RepresentationId" = representation_id AND "ContentSize" <> rvz."LogicalBytes")) THEN
        RAISE EXCEPTION 'RVZ geometry must describe its exact whole output' USING ERRCODE = '23514';
    END IF;
    IF encoding = 'Chd' THEN
        SELECT count(*),coalesce(sum(octet_length("Payload")),0),
            sha256(coalesce(string_agg(substring(int8send("Tag") FROM 5) || set_byte(decode('00','hex'),0,"Flags") ||
                int4send(octet_length("Payload")) || "Payload", decode('','hex') ORDER BY "Ordinal"),decode('','hex')))
            INTO entries,metadata_bytes,metadata_hash FROM romd."ChdMetadataEntries" WHERE "RepresentationId" = representation_id;
        SELECT f."Size" INTO artifact_bytes FROM romd."Representations" r JOIN romd."Files" f ON f."Id" = r."FileId"
            WHERE r."Id" = representation_id;
        IF artifact_bytes < 124 OR metadata_bytes + entries * 16 + 124 > artifact_bytes OR
            metadata_bytes > 524288 OR metadata_hash <> chd."MetadataSha256" OR EXISTS (
            SELECT 1 FROM (SELECT "Ordinal",row_number() OVER (ORDER BY "Ordinal") - 1 AS expected
                FROM romd."ChdMetadataEntries" WHERE "RepresentationId" = representation_id) ordered WHERE "Ordinal" <> expected
        ) THEN
            RAISE EXCEPTION 'CHD metadata must retain the exact ordered bounded observation' USING ERRCODE = '23514';
        END IF;
        SELECT "LogicalFrames" INTO logical_frames FROM romd."ReconstructionManifests" WHERE "RepresentationId" = representation_id;
        IF chd."HasParent" OR (logical_frames IS NOT NULL AND
            (chd."UnitBytes" <> 2448 OR chd."LogicalBytes" <> logical_frames * 2448 OR entries = 0)) OR
            (logical_frames IS NULL AND EXISTS (SELECT 1 FROM romd."RepresentationOutputs"
                WHERE "RepresentationId" = representation_id AND "ContentSize" <> chd."LogicalBytes")) THEN
            RAISE EXCEPTION 'CHD container geometry contradicts its reconstructed layout' USING ERRCODE = '23514';
        END IF;
    END IF;
    IF EXISTS (SELECT 1 FROM romd."Verifications" WHERE "RepresentationId" = representation_id AND
        "ObservedParametersSha256" IS DISTINCT FROM romd.media_parameter_fingerprint(representation_id)) THEN
        RAISE EXCEPTION 'Verification must bind its independently observed container parameters' USING ERRCODE = '23514';
    END IF;
    IF EXISTS (
        SELECT 1 FROM romd."Verifications" v WHERE v."RepresentationId" = representation_id AND (
            (encoding = 'Chd' AND (v."ComputedRawSha1" IS NULL OR v."ComputedOverallSha1" IS NULL OR
                v."ComputedRawSha1" IS DISTINCT FROM chd."StoredRawSha1" OR v."ComputedOverallSha1" IS DISTINCT FROM chd."StoredOverallSha1" OR
                (logical_frames IS NULL AND EXISTS (SELECT 1 FROM romd."VerificationOutputs" o
                    WHERE o."VerificationId" = v."Id" AND o."Sha1" <> v."ComputedRawSha1")))) OR
            (encoding <> 'Chd' AND (v."ComputedRawSha1" IS NOT NULL OR v."ComputedOverallSha1" IS NOT NULL)))
    ) THEN
        RAISE EXCEPTION 'CHD verification requires matching independently computed container hashes' USING ERRCODE = '23514';
    END IF;
    PERFORM romd.validate_media_artifact_agreement(representation_id);
END $body$;
DO $body$
DECLARE target text; runtime text;
BEGIN
    FOREACH target IN ARRAY ARRAY['ChdContainerParameters','ChdMetadataEntries','RvzContainerParameters'] LOOP
        EXECUTE format('CREATE TRIGGER %I BEFORE INSERT OR UPDATE OR DELETE ON romd.%I FOR EACH ROW EXECUTE FUNCTION romd.guard_media_fact_manifest()',
            'TR_' || target || '_ImmutableFacts',target);
        EXECUTE format('CREATE CONSTRAINT TRIGGER %I AFTER INSERT OR UPDATE OR DELETE ON romd.%I DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION romd.check_media_fact_graph()',
            'TR_' || target || '_CompleteMediaGraph',target);
        EXECUTE format('REVOKE ALL ON romd.%I FROM PUBLIC',target);
        FOREACH runtime IN ARRAY ARRAY['romd_admin','romd_consumer'] LOOP
            IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = runtime) THEN
                EXECUTE format('REVOKE INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER ON romd.%I FROM %I',target,runtime);
            END IF;
        END LOOP;
    END LOOP;
END $body$;
