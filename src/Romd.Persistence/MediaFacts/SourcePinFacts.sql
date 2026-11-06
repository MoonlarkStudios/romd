-- Admission needs lock authority without granting API hosts arbitrary writes to retained facts.
-- Callers preserve the global order: all logical ROMs, sorted representations, then sorted files.
CREATE FUNCTION romd.lock_media_logical_rom(rom_id integer) RETURNS void
    LANGUAGE plpgsql SECURITY DEFINER SET search_path = '' AS $body$
BEGIN
    IF rom_id IS NULL OR rom_id <= 0 OR current_setting('transaction_isolation') <> 'read committed' THEN
        RAISE EXCEPTION 'Logical media locking requires a positive identity and ReadCommitted transaction' USING ERRCODE = '23514';
    END IF;
    PERFORM 1 FROM romd."Roms" WHERE "Id" = rom_id FOR UPDATE;
END $body$;
CREATE FUNCTION romd.lock_media_representation(representation_id integer) RETURNS void
    LANGUAGE plpgsql SECURITY DEFINER SET search_path = '' AS $body$
BEGIN
    IF representation_id IS NULL OR representation_id <= 0 OR current_setting('transaction_isolation') <> 'read committed' THEN
        RAISE EXCEPTION 'Representation locking requires a positive identity and ReadCommitted transaction' USING ERRCODE = '23514';
    END IF;
    PERFORM pg_advisory_xact_lock(9415,representation_id);
    PERFORM 1 FROM romd."Representations" WHERE "Id" = representation_id FOR NO KEY UPDATE;
END $body$;
REVOKE ALL ON FUNCTION romd.lock_media_logical_rom(integer),romd.lock_media_representation(integer) FROM PUBLIC;
DO $body$
DECLARE runtime text;
BEGIN
    FOREACH runtime IN ARRAY ARRAY['romd_admin','romd_worker'] LOOP
        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = runtime) THEN
            EXECUTE format('GRANT EXECUTE ON FUNCTION romd.lock_media_logical_rom(integer),romd.lock_media_representation(integer) TO %I',runtime);
        END IF;
    END LOOP;
END $body$;

-- Cleanup releases the interpretation and its complete fact graph before ordinary ledger/CAS collection.
CREATE FUNCTION romd.delete_unowned_media_representation(representation_id integer) RETURNS boolean LANGUAGE plpgsql SET search_path = '' AS $body$
DECLARE removed integer;
BEGIN
    IF representation_id <= 0 OR current_setting('transaction_isolation') <> 'read committed' THEN
        RAISE EXCEPTION 'Representation cleanup requires a positive identity and ReadCommitted transaction' USING ERRCODE = '23514';
    END IF;
    IF NOT pg_try_advisory_xact_lock(9415,representation_id) THEN RETURN false; END IF;
    PERFORM 1 FROM romd."Representations" WHERE "Id" = representation_id FOR UPDATE;
    IF NOT FOUND OR EXISTS (SELECT 1 FROM romd."RepresentationOutputs" WHERE "RepresentationId" = representation_id AND "RomId" IS NOT NULL) OR
        EXISTS (SELECT 1 FROM romd."CdArchiveOwners" WHERE "RepresentationId" = representation_id) OR
        EXISTS (SELECT 1 FROM romd."CatalogCdLayoutBindings" WHERE "RepresentationId" = representation_id) OR
        EXISTS (SELECT 1 FROM romd."OriginalDiscLayoutFiles" member JOIN romd."Representations" representation ON representation."FileId" = member."FileId" WHERE representation."Id" = representation_id) OR
        EXISTS (SELECT 1 FROM romd."MediaReconciliationPins" WHERE "RepresentationId" = representation_id) THEN RETURN false; END IF;
    PERFORM 1 FROM romd."Files" WHERE "Id" IN (SELECT "FileId" FROM romd."Representations" WHERE "Id" = representation_id
        UNION SELECT "StoredFileId" FROM romd."ReconstructionMembers" WHERE "RepresentationId" = representation_id AND "StoredFileId" IS NOT NULL)
        ORDER BY "Id" FOR NO KEY UPDATE;
    DELETE FROM romd."Representations" r WHERE r."Id" = representation_id AND
        NOT EXISTS (SELECT 1 FROM romd."RepresentationOutputs" WHERE "RepresentationId" = r."Id" AND "RomId" IS NOT NULL) AND
        NOT EXISTS (SELECT 1 FROM romd."CdArchiveOwners" WHERE "RepresentationId" = r."Id") AND
        NOT EXISTS (SELECT 1 FROM romd."CatalogCdLayoutBindings" WHERE "RepresentationId" = r."Id") AND
        NOT EXISTS (SELECT 1 FROM romd."OriginalDiscLayoutFiles" WHERE "FileId" = r."FileId") AND
        NOT EXISTS (SELECT 1 FROM romd."MediaReconciliationPins" WHERE "RepresentationId" = r."Id");
    GET DIAGNOSTICS removed = ROW_COUNT;
    RETURN removed = 1;
END $body$;
REVOKE ALL ON FUNCTION romd.delete_unowned_media_representation(integer) FROM PUBLIC;
DO $body$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'romd_admin') THEN REVOKE ALL ON FUNCTION romd.delete_unowned_media_representation(integer) FROM romd_admin; END IF;
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'romd_consumer') THEN REVOKE ALL ON FUNCTION romd.delete_unowned_media_representation(integer) FROM romd_consumer; END IF;
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'romd_worker') THEN GRANT EXECUTE ON FUNCTION romd.delete_unowned_media_representation(integer) TO romd_worker; END IF;
END $body$;
