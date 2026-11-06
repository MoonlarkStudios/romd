-- Applied on every worker provisioning/startup, independently of the immutable baseline. Keep this script idempotent.
-- Reapply fact authority after provisioning/default grants; API hosts may withdraw logical ROMs but cannot manufacture observations.
REVOKE ALL ON romd."Verifications",romd."VerificationOutputs" FROM PUBLIC;
REVOKE ALL ON FUNCTION romd.publish_media_verification(uuid,integer,bytea,bigint,text,text,bytea,integer,text,text,bigint,bytea,bytea,bytea,jsonb) FROM PUBLIC;
REVOKE ALL ON FUNCTION romd.delete_unowned_media_representation(integer) FROM PUBLIC;
REVOKE ALL ON FUNCTION romd.lock_media_logical_rom(integer),romd.lock_media_representation(integer) FROM PUBLIC;
REVOKE ALL ON FUNCTION romd.read_disc_reference_assertions(integer[]) FROM PUBLIC;
REVOKE ALL ON FUNCTION romd.guard_original_layout_media_dependency(),romd.guard_layout_owned_representation_delete() FROM PUBLIC;
DO $body$
DECLARE runtime text; protected text := 'romd."Representations",romd."RepresentationOutputs",romd."ReconstructionManifests",romd."ReconstructionTracks",romd."ReconstructionMembers",romd."ReconstructionMemberTracks",romd."MediaReconciliationAttempts",romd."MediaReconciliationInputs",romd."MediaReconciliationPins",romd."MediaReconciliationFailures",romd."MediaReconciliationOutcomes",romd."MediaReconciliationSelectionItems",romd."ChdContainerParameters",romd."ChdMetadataEntries",romd."RvzContainerParameters",romd."LogicalInspections",romd."Ps2BootObservations",romd."RepresentationCreations",romd."CreationSources",romd."CdBootInspections",romd."CdEncodingObservations",romd."CdArchiveOwners",romd."CatalogCdLayoutBindings"';
BEGIN
    FOREACH runtime IN ARRAY ARRAY['romd_worker','romd_admin','romd_consumer'] LOOP
        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = runtime) THEN
            EXECUTE format('REVOKE ALL ON romd."Verifications",romd."VerificationOutputs" FROM %I',runtime);
            EXECUTE format('GRANT SELECT ON romd."Verifications",romd."VerificationOutputs" TO %I',runtime);
            EXECUTE format('GRANT EXECUTE ON FUNCTION romd.read_disc_reference_assertions(integer[]) TO %I',runtime);
            EXECUTE format('REVOKE ALL ON FUNCTION romd.publish_media_verification(uuid,integer,bytea,bigint,text,text,bytea,integer,text,text,bigint,bytea,bytea,bytea,jsonb),romd.delete_unowned_media_representation(integer) FROM %I',runtime);
            EXECUTE format('REVOKE TRUNCATE ON %s,romd."Roms" FROM %I',protected,runtime);
            EXECUTE format('REVOKE ALL ON FUNCTION romd.lock_media_logical_rom(integer),romd.lock_media_representation(integer) FROM %I',runtime);
            EXECUTE format('REVOKE ALL ON FUNCTION romd.guard_original_layout_media_dependency(),romd.guard_layout_owned_representation_delete() FROM %I',runtime);
            IF runtime IN ('romd_admin','romd_worker') THEN
                EXECUTE format('GRANT EXECUTE ON FUNCTION romd.lock_media_logical_rom(integer),romd.lock_media_representation(integer) TO %I',runtime);
            END IF;
            IF runtime <> 'romd_worker' THEN
                EXECUTE format('REVOKE INSERT,UPDATE,DELETE ON %s FROM %I',protected,runtime);
                EXECUTE format('REVOKE INSERT,UPDATE ON romd."Roms" FROM %I',runtime);
            END IF;
        END IF;
    END LOOP;
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'romd_worker') THEN
        GRANT EXECUTE ON FUNCTION romd.publish_media_verification(uuid,integer,bytea,bigint,text,text,bytea,integer,text,text,bigint,bytea,bytea,bytea,jsonb),
            romd.delete_unowned_media_representation(integer) TO romd_worker;
    END IF;
END $body$;

-- API acceptance may enqueue/adopt demand and request cancellation; execution observations remain worker-owned.
REVOKE ALL ON FUNCTION romd.require_media_reconciliation_fence(uuid,uuid),romd.guard_media_reconciliation_input(),romd.validate_media_reconciliation_job(uuid),romd.guard_media_reconciliation_commit() FROM PUBLIC;
DO $body$
DECLARE runtime text;
BEGIN
    FOREACH runtime IN ARRAY ARRAY['romd_admin','romd_consumer','romd_worker'] LOOP
        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = runtime) THEN
            EXECUTE format('REVOKE TRUNCATE ON romd."MediaReconciliationJobs",romd."MediaReconciliationSubjects",romd."MediaReconciliationRequests" FROM %I',runtime);
            EXECUTE format('REVOKE ALL ON FUNCTION romd.require_media_reconciliation_fence(uuid,uuid),romd.guard_media_reconciliation_input(),romd.validate_media_reconciliation_job(uuid),romd.guard_media_reconciliation_commit() FROM %I',runtime);
            IF runtime = 'romd_consumer' THEN
                EXECUTE format('REVOKE INSERT,UPDATE,DELETE ON romd."MediaReconciliationJobs",romd."MediaReconciliationSubjects",romd."MediaReconciliationRequests" FROM %I',runtime);
            ELSIF runtime = 'romd_admin' THEN
                REVOKE UPDATE,DELETE ON romd."MediaReconciliationJobs",romd."MediaReconciliationSubjects",romd."MediaReconciliationRequests" FROM romd_admin;
                GRANT UPDATE ("CancelRequested") ON romd."MediaReconciliationJobs" TO romd_admin;
                GRANT UPDATE ("RequestedVersion","LatestJobId","ConversionRequested") ON romd."MediaReconciliationSubjects" TO romd_admin;
            ELSE
                GRANT EXECUTE ON FUNCTION romd.require_media_reconciliation_fence(uuid,uuid) TO romd_worker;
            END IF;
        END IF;
    END LOOP;
END $body$;

-- Only the worker publishes or withdraws catalog CD layout bindings; updates cannot rewrite provenance.
REVOKE ALL ON FUNCTION romd.guard_catalog_cd_layout_binding(),romd.guard_catalog_cd_layout_representation_delete() FROM PUBLIC;
DO $body$
DECLARE runtime text;
BEGIN
    FOREACH runtime IN ARRAY ARRAY['romd_worker','romd_admin','romd_consumer'] LOOP
        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = runtime) THEN
            EXECUTE format('REVOKE UPDATE,TRUNCATE ON romd."CatalogCdLayoutBindings" FROM %I',runtime);
            EXECUTE format('REVOKE ALL ON romd."CurrentCatalogCdLayoutBindings" FROM %I',runtime);
            EXECUTE format('GRANT SELECT ON romd."CurrentCatalogCdLayoutBindings" TO %I',runtime);
            EXECUTE format('REVOKE ALL ON FUNCTION romd.guard_catalog_cd_layout_binding(),romd.guard_catalog_cd_layout_representation_delete() FROM %I',runtime);
        END IF;
    END LOOP;
END $body$;
