-- A layout's current archive is a durable owner independent of logical ROM preferences and job-history retention.
CREATE TABLE romd."CdArchiveOwners" (
    "LayoutId" integer PRIMARY KEY REFERENCES romd."OriginalDiscLayouts" ("Id") ON DELETE CASCADE,
    "RepresentationId" integer NOT NULL REFERENCES romd."Representations" ("Id") ON DELETE RESTRICT,
    "VerificationId" uuid NOT NULL,
    "PublicationId" uuid NOT NULL CHECK ("PublicationId" <> '00000000-0000-0000-0000-000000000000'),
    "LayoutRevision" uuid NOT NULL CHECK ("LayoutRevision" <> '00000000-0000-0000-0000-000000000000'),
    "PlatformId" integer NOT NULL CHECK ("PlatformId" > 0),
    "ReleaseId" integer NOT NULL CHECK ("ReleaseId" > 0),
    "CatalogFingerprint" varchar(120) NOT NULL CHECK (btrim("CatalogFingerprint") <> '' AND "CatalogFingerprint" !~ '[[:cntrl:]]'),
    "EntryPoint" varchar(240) NOT NULL CHECK (length("EntryPoint") > 0 AND "EntryPoint" !~ '[[:cntrl:]/]' AND position(chr(92) IN "EntryPoint") = 0),
    "PublishedAtUtcTicks" bigint NOT NULL CHECK ("PublishedAtUtcTicks" BETWEEN 0 AND 3155378975999999999),
    FOREIGN KEY ("VerificationId","RepresentationId") REFERENCES romd."Verifications" ("Id","RepresentationId") ON DELETE RESTRICT
);
CREATE INDEX "IX_CdArchiveOwners_RepresentationId" ON romd."CdArchiveOwners" ("RepresentationId");
CREATE FUNCTION romd.guard_cd_archive_owner() RETURNS trigger LANGUAGE plpgsql SET search_path = '' AS $body$
BEGIN
    IF TG_OP = 'UPDATE' AND NEW."LayoutId" <> OLD."LayoutId" THEN
        RAISE EXCEPTION 'An archive owner cannot move to another original layout' USING ERRCODE = '23514';
    END IF;
    IF current_setting('transaction_isolation') <> 'read committed' THEN
        RAISE EXCEPTION 'Archive publication requires ReadCommitted authority' USING ERRCODE = '23514';
    END IF;
    -- The caller takes the topology fence and these scope locks before ROM/representation/file locks.
    PERFORM 1 FROM romd."OriginalDiscLayouts" WHERE "Id" = NEW."LayoutId" FOR NO KEY UPDATE;
    PERFORM 1 FROM romd."CatalogReleases" WHERE "Id" = NEW."ReleaseId" FOR NO KEY UPDATE;
    IF NOT EXISTS (SELECT 1 FROM romd."OriginalDiscLayouts" l JOIN romd."CatalogReleases" r ON r."Id" = NEW."ReleaseId"
        WHERE l."Id" = NEW."LayoutId" AND l."Revision" = NEW."LayoutRevision" AND l."Issue" IS NULL AND
            (l."State" = 'pending' OR l."State" = 'accepted' AND l."ReleaseId" = NEW."ReleaseId") AND
            r."PlatformId" = NEW."PlatformId" AND r."Fingerprint" = NEW."CatalogFingerprint" AND NOT r."HasTitleConflict") OR
        NOT EXISTS (SELECT 1 FROM romd."Verifications" v WHERE v."Id" = NEW."VerificationId" AND
            v."RepresentationId" = NEW."RepresentationId" AND v."DecoderRid" IS NOT NULL AND
            v."CheckedProfile" IN ('chd-cd-whole-files/v1','chd-cd-retained-files/v2')) OR
        NOT EXISTS (SELECT 1 FROM romd."ReconstructionMembers" m JOIN romd."OriginalDiscLayouts" l ON l."Id" = NEW."LayoutId"
            WHERE m."RepresentationId" = NEW."RepresentationId" AND m."StoredRole" = 'Cue' AND m."Path" = NEW."EntryPoint" AND
                m."StoredFileId" = l."DescriptorFileId") OR
        EXISTS (SELECT 1 FROM romd."RepresentationOutputs" WHERE "RepresentationId" = NEW."RepresentationId" AND "RomId" IS NULL) THEN
        RAISE EXCEPTION 'An archive owner requires the current exact layout/catalog scope, authentic descriptor and independently compared CD verification' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END $body$;
CREATE TRIGGER "TR_CdArchiveOwners_Authority" BEFORE INSERT OR UPDATE ON romd."CdArchiveOwners"
    FOR EACH ROW EXECUTE FUNCTION romd.guard_cd_archive_owner();
DO $body$
DECLARE runtime text;
BEGIN
    FOREACH runtime IN ARRAY ARRAY['romd_admin','romd_consumer'] LOOP
        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = runtime) THEN
            EXECUTE format('REVOKE INSERT,UPDATE,DELETE,TRUNCATE ON romd."CdArchiveOwners" FROM %I',runtime);
        END IF;
    END LOOP;
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'romd_worker') THEN REVOKE TRUNCATE ON romd."CdArchiveOwners" FROM romd_worker; END IF;
END $body$;

-- A retained original-layout dependency owns the interpretation needed to rebuild its bytes, including a CD member
-- after logical withdrawal. Admission serializes with graph collection rather than relying on the ledger FK alone.
CREATE FUNCTION romd.guard_original_layout_media_dependency() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path = '' AS $body$
DECLARE representation_id integer;
BEGIN
    IF TG_OP = 'UPDATE' AND (NEW."FileId",NEW."RomId") IS NOT DISTINCT FROM (OLD."FileId",OLD."RomId") THEN RETURN NEW; END IF;
    IF current_setting('transaction_isolation') <> 'read committed' THEN
        RAISE EXCEPTION 'Original layout media admission requires ReadCommitted' USING ERRCODE = '23514';
    END IF;
    PERFORM 1 FROM romd."Roms" WHERE "Id" = NEW."RomId" FOR NO KEY UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'Original layout dependency requires its retained logical identity' USING ERRCODE = '23514'; END IF;
    FOR representation_id IN SELECT DISTINCT representation."Id" FROM romd."Representations" representation
        JOIN romd."RepresentationOutputs" output ON output."RepresentationId" = representation."Id"
        WHERE representation."FileId" = NEW."FileId" AND output."RomId" = NEW."RomId" ORDER BY representation."Id" LOOP
        PERFORM pg_advisory_xact_lock(9415,representation_id);
        PERFORM 1 FROM romd."Representations" WHERE "Id" = representation_id FOR NO KEY UPDATE;
    END LOOP;
    PERFORM 1 FROM romd."Files" WHERE "Id" = NEW."FileId" FOR NO KEY UPDATE;
    IF NOT EXISTS (SELECT 1 FROM romd."Representations" representation JOIN romd."RepresentationOutputs" output ON output."RepresentationId" = representation."Id"
        JOIN romd."Verifications" proof ON proof."RepresentationId" = representation."Id"
        WHERE representation."FileId" = NEW."FileId" AND output."RomId" = NEW."RomId" AND
            (representation."Encoding" = 'Identity' AND output."MemberOrdinal" IS NULL OR output."MemberOrdinal" IS NOT NULL)) THEN
        RAISE EXCEPTION 'Original layout dependency requires its exact complete measured original or CD member backing' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END $body$;
CREATE TRIGGER "TR_OriginalDiscLayoutFiles_MediaDependency" BEFORE INSERT OR UPDATE ON romd."OriginalDiscLayoutFiles"
    FOR EACH ROW EXECUTE FUNCTION romd.guard_original_layout_media_dependency();

CREATE FUNCTION romd.guard_layout_owned_representation_delete() RETURNS trigger LANGUAGE plpgsql SET search_path = '' AS $body$
BEGIN
    IF EXISTS (SELECT 1 FROM romd."OriginalDiscLayoutFiles" WHERE "FileId" = OLD."FileId") THEN
        RAISE EXCEPTION 'Original layout dependencies retain their reconstruction facts and every decoder measurement' USING ERRCODE = '23514';
    END IF;
    RETURN OLD;
END $body$;
CREATE TRIGGER "TR_Representations_LayoutOwnership" BEFORE DELETE ON romd."Representations"
    FOR EACH ROW EXECUTE FUNCTION romd.guard_layout_owned_representation_delete();
REVOKE ALL ON FUNCTION romd.guard_original_layout_media_dependency(),romd.guard_layout_owned_representation_delete() FROM PUBLIC;
