CREATE FUNCTION romd.require_media_reconciliation_fence(job_id uuid, token uuid) RETURNS void
LANGUAGE plpgsql SET search_path = '' AS $body$
BEGIN
    IF current_setting('transaction_isolation') <> 'read committed' THEN
        RAISE EXCEPTION 'Reconciliation mutations require ReadCommitted' USING ERRCODE = '23514';
    END IF;
    PERFORM 1 FROM romd."Jobs" WHERE "Id" = job_id AND "JobType" = 'media-reconciliation' AND "CompletedAt" IS NULL AND
        "ExecutionFenceToken" = token AND "ExecutionLeaseExpiresAtUtc" > floor(extract(epoch FROM clock_timestamp()) * 1000)::bigint FOR NO KEY UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'Reconciliation mutation lost its execution fence' USING ERRCODE = '23514'; END IF;
END; $body$;

CREATE FUNCTION romd.guard_media_reconciliation_definition() RETURNS trigger LANGUAGE plpgsql SET search_path = '' AS $body$
BEGIN
    IF TG_OP <> 'INSERT' THEN
        IF TG_OP = 'DELETE' OR
            (to_jsonb(NEW) - ARRAY['CancelRequested','CursorKind','CursorId','SelectedCount','CreatedCount','AdoptedCount','SkippedCount','CompletedCount','FailedCount','EnumerationComplete']) IS DISTINCT FROM
            (to_jsonb(OLD) - ARRAY['CancelRequested','CursorKind','CursorId','SelectedCount','CreatedCount','AdoptedCount','SkippedCount','CompletedCount','FailedCount','EnumerationComplete']) OR
            OLD."CancelRequested" AND NOT NEW."CancelRequested" OR
            OLD."RequestMode" = 'Selection' AND (NEW."SelectedCount" < OLD."SelectedCount" OR NEW."CreatedCount" < OLD."CreatedCount" OR
                NEW."AdoptedCount" < OLD."AdoptedCount" OR NEW."SkippedCount" < OLD."SkippedCount" OR
                NEW."CompletedCount" < OLD."CompletedCount" OR NEW."FailedCount" < OLD."FailedCount" OR OLD."EnumerationComplete" AND NOT NEW."EnumerationComplete" OR
                OLD."CursorId" IS NOT NULL AND (NEW."CursorId" IS NULL OR
                    (array_position(ARRAY['LogicalRom','OriginalLayout','Representation','CatalogCdLayout'],NEW."CursorKind"),NEW."CursorId") <
                    (array_position(ARRAY['LogicalRom','OriginalLayout','Representation','CatalogCdLayout'],OLD."CursorKind"),OLD."CursorId"))) THEN
            RAISE EXCEPTION 'Reconciliation scope, original ownership and selection history are immutable' USING ERRCODE = '23514';
        END IF;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM romd."Jobs" WHERE "Id" = NEW."JobId" AND "JobType" = 'media-reconciliation') OR
        NEW."OriginSelectionJobId" IS NOT NULL AND NOT EXISTS (SELECT 1 FROM romd."MediaReconciliationJobs" parent
            JOIN romd."Jobs" root ON root."Id" = parent."JobId" WHERE parent."JobId" = NEW."OriginSelectionJobId" AND parent."RequestMode" = 'Selection' AND root."CompletedAt" IS NULL) THEN
        RAISE EXCEPTION 'Reconciliation definition must bind its exact family and active original selection owner' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END; $body$;
CREATE TRIGGER "TR_MediaReconciliationJobs_Identity" BEFORE INSERT OR UPDATE OR DELETE ON romd."MediaReconciliationJobs"
    FOR EACH ROW EXECUTE FUNCTION romd.guard_media_reconciliation_definition();

CREATE FUNCTION romd.guard_media_reconciliation_subject() RETURNS trigger LANGUAGE plpgsql SET search_path = '' AS $body$
BEGIN
    IF TG_OP = 'DELETE' THEN RAISE EXCEPTION 'Subject demand history is retained' USING ERRCODE = '23514'; END IF;
    IF TG_OP = 'UPDATE' THEN
        IF (NEW."SubjectKind",NEW."SubjectId") IS DISTINCT FROM (OLD."SubjectKind",OLD."SubjectId") OR
            NEW."RequestedVersion" < OLD."RequestedVersion" OR NEW."AcknowledgedVersion" < OLD."AcknowledgedVersion" OR
            OLD."ConversionRequested" AND NOT NEW."ConversionRequested" AND OLD."AcknowledgedVersion" <> OLD."RequestedVersion" OR
            NEW."LatestJobId" <> OLD."LatestJobId" AND (NEW."RequestedVersion" <= OLD."RequestedVersion" OR
                NOT EXISTS (SELECT 1 FROM romd."Jobs" WHERE "Id" = OLD."LatestJobId" AND "CompletedAt" IS NOT NULL)) THEN
            RAISE EXCEPTION 'Subject demand and active explicit conversion cannot regress or replace active work' USING ERRCODE = '23514';
        END IF;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM romd."MediaReconciliationJobs" WHERE "JobId" = NEW."LatestJobId" AND "RequestMode" = 'Subject' AND
            "SubjectKind" = NEW."SubjectKind" AND "SubjectId" = NEW."SubjectId") OR
        NEW."AcknowledgedVersion" > 0 AND NOT EXISTS (SELECT 1 FROM romd."MediaReconciliationOutcomes" o
            JOIN romd."MediaReconciliationJobs" j ON j."JobId" = o."JobId" WHERE j."SubjectKind" = NEW."SubjectKind" AND j."SubjectId" = NEW."SubjectId" AND
                o."AcknowledgedVersion" = NEW."AcknowledgedVersion") THEN
        RAISE EXCEPTION 'Subject acknowledgement requires its exact durable terminal outcome' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END; $body$;
CREATE TRIGGER "TR_MediaReconciliationSubjects_Demand" BEFORE INSERT OR UPDATE OR DELETE ON romd."MediaReconciliationSubjects"
    FOR EACH ROW EXECUTE FUNCTION romd.guard_media_reconciliation_subject();

CREATE FUNCTION romd.guard_media_reconciliation_request() RETURNS trigger LANGUAGE plpgsql SET search_path = '' AS $body$
DECLARE definition record; previous record;
BEGIN
    IF TG_OP <> 'INSERT' THEN RAISE EXCEPTION 'Accepted request identity and actor are immutable' USING ERRCODE = '23514'; END IF;
    SELECT d.*,r."CompletedAt" INTO definition FROM romd."MediaReconciliationJobs" d JOIN romd."Jobs" r ON r."Id" = d."JobId" WHERE d."JobId" = NEW."JobId";
    IF NOT FOUND OR definition."CompletedAt" IS NOT NULL OR
        definition."RequestMode" = 'Subject' AND NEW."RequestedVersion" <= 0 OR
        definition."RequestMode" = 'Selection' AND (NEW."RequestedVersion" <> 0 OR NEW."ConversionRequested" IS DISTINCT FROM definition."ConversionRequested") OR
        NEW."OriginSelectionJobId" IS NOT NULL AND NOT EXISTS (SELECT 1 FROM romd."MediaReconciliationJobs" parent JOIN romd."Jobs" root ON root."Id" = parent."JobId"
            WHERE parent."JobId" = NEW."OriginSelectionJobId" AND parent."RequestMode" = 'Selection' AND NOT parent."CancelRequested" AND root."CompletedAt" IS NULL) THEN
        RAISE EXCEPTION 'Accepted requests must bind the current exact active demand' USING ERRCODE = '23514';
    END IF;
    IF NEW."RetryOfJobId" IS NOT NULL THEN
        SELECT d.*,r."CompletedAt" INTO previous FROM romd."MediaReconciliationJobs" d JOIN romd."Jobs" r ON r."Id" = d."JobId" WHERE d."JobId" = NEW."RetryOfJobId";
        IF NOT FOUND OR previous."CompletedAt" IS NULL OR previous."RequestMode" <> definition."RequestMode" OR
            definition."RequestMode" = 'Subject' AND ((previous."SubjectKind",previous."SubjectId") IS DISTINCT FROM (definition."SubjectKind",definition."SubjectId") OR
                NEW."ConversionRequested" IS DISTINCT FROM EXISTS (SELECT 1 FROM romd."MediaReconciliationRequests" request WHERE request."JobId" = previous."JobId" AND request."ConversionRequested")) OR
            definition."RequestMode" = 'Selection' AND
                (previous."Filter",previous."KindId",previous."PlatformId",previous."UpperRomId",previous."UpperLayoutId",previous."UpperRepresentationId",previous."UpperCatalogFileId",previous."Limit",previous."ConversionRequested") IS DISTINCT FROM
                (definition."Filter",definition."KindId",definition."PlatformId",definition."UpperRomId",definition."UpperLayoutId",definition."UpperRepresentationId",definition."UpperCatalogFileId",definition."Limit",definition."ConversionRequested") THEN
            RAISE EXCEPTION 'Retry must retain the exact terminal subject intent or finite selection scope' USING ERRCODE = '23514';
        END IF;
    END IF;
    RETURN NEW;
END; $body$;
CREATE TRIGGER "TR_MediaReconciliationRequests_Identity" BEFORE INSERT OR UPDATE OR DELETE ON romd."MediaReconciliationRequests"
    FOR EACH ROW EXECUTE FUNCTION romd.guard_media_reconciliation_request();

CREATE FUNCTION romd.guard_media_reconciliation_attempt() RETURNS trigger LANGUAGE plpgsql SET search_path = '' AS $body$
DECLARE proof record; source_count integer;
BEGIN
    IF TG_OP = 'DELETE' THEN RAISE EXCEPTION 'Attempt observations are retained' USING ERRCODE = '23514'; END IF;
    PERFORM romd.require_media_reconciliation_fence(NEW."JobId",NEW."ExecutionFenceToken");
    IF TG_OP = 'UPDATE' AND (OLD."Outcome" IS NOT NULL OR
        (to_jsonb(NEW) - ARRAY['ExecutionFenceToken','Outcome','OutcomeCode','Deterministic','ResultRepresentationId','ResultVerificationId','ResultCreationId','ResultInspectionId','CompletedAtUtcTicks','StoppedAtUtcTicks']) IS DISTINCT FROM
        (to_jsonb(OLD) - ARRAY['ExecutionFenceToken','Outcome','OutcomeCode','Deterministic','ResultRepresentationId','ResultVerificationId','ResultCreationId','ResultInspectionId','CompletedAtUtcTicks','StoppedAtUtcTicks'])) THEN
        RAISE EXCEPTION 'Captured attempts and terminal observations are immutable' USING ERRCODE = '23514';
    END IF;
    IF TG_OP = 'INSERT' AND (NEW."Outcome" IS NOT NULL OR NOT EXISTS (SELECT 1 FROM romd."MediaReconciliationSubjects" s
            JOIN romd."MediaReconciliationJobs" d ON d."JobId" = s."LatestJobId" WHERE d."JobId" = NEW."JobId" AND d."RequestMode" = 'Subject' AND
                NEW."RequestedVersion" > s."AcknowledgedVersion" AND NEW."RequestedVersion" <= s."RequestedVersion") OR
            NEW."Number" <> coalesce((SELECT max(a."Number") + 1 FROM romd."MediaReconciliationAttempts" a WHERE a."JobId" = NEW."JobId"),1)) THEN
        RAISE EXCEPTION 'A captured attempt must extend its active subject demand' USING ERRCODE = '23514';
    END IF;
    IF TG_OP = 'INSERT' AND EXISTS (SELECT 1 FROM romd."MediaReconciliationJobs" definition WHERE definition."JobId" = NEW."JobId" AND
        (definition."SubjectKind" = 'Representation' AND NEW."Operation" <> 'Verify' OR
         (definition."SubjectKind" = 'OriginalLayout') IS DISTINCT FROM (NEW."OriginalLayoutId" IS NOT NULL) OR
         (definition."SubjectKind" = 'CatalogCdLayout') IS DISTINCT FROM (NEW."CatalogFileId" IS NOT NULL) OR
         definition."SubjectKind" = 'OriginalLayout' AND definition."SubjectId" <> NEW."OriginalLayoutId" OR
         definition."SubjectKind" = 'CatalogCdLayout' AND definition."SubjectId" <> NEW."CatalogFileId")) THEN
        RAISE EXCEPTION 'Attempt operation and exact layout context must belong to its subject namespace' USING ERRCODE = '23514';
    END IF;
    IF TG_OP = 'INSERT' AND NEW."OriginalLayoutId" IS NOT NULL THEN
        PERFORM pg_advisory_xact_lock(hashtext('romd.catalog-topology'));
        PERFORM 1 FROM romd."OriginalDiscLayouts" WHERE "Id" = NEW."OriginalLayoutId" FOR NO KEY UPDATE;
        PERFORM 1 FROM romd."CatalogReleases" WHERE "Id" = NEW."ReleaseId" FOR NO KEY UPDATE;
        IF NOT EXISTS (SELECT 1 FROM romd."OriginalDiscLayouts" layout JOIN romd."CatalogReleases" release ON release."Id" = NEW."ReleaseId"
            WHERE layout."Id" = NEW."OriginalLayoutId" AND layout."Revision" = NEW."LayoutRevision" AND layout."Issue" IS NULL AND
                (layout."State" = 'pending' OR layout."State" = 'accepted' AND layout."ReleaseId" = NEW."ReleaseId" AND layout."Fingerprint" = NEW."CatalogFingerprint") AND
                release."PlatformId" = NEW."PlatformId" AND release."Fingerprint" = NEW."CatalogFingerprint" AND NOT release."HasTitleConflict" AND
                regexp_replace(replace(layout."EntryPoint",chr(92),'/'),'^.*/','') = NEW."EntryPoint") THEN
            RAISE EXCEPTION 'Attempt layout and catalog context changed before capture' USING ERRCODE = '23514', CONSTRAINT = 'media_reconciliation_source_changed';
        END IF;
    END IF;
    IF TG_OP = 'INSERT' AND NEW."CatalogFileId" IS NOT NULL THEN
        PERFORM pg_advisory_xact_lock(hashtext('romd.catalog-topology'));
        PERFORM 1 FROM romd."CatalogReleases" WHERE "Id"=NEW."ReleaseId" FOR NO KEY UPDATE;
        PERFORM 1 FROM romd."CatalogCdLayoutBindings" WHERE "CatalogFileId"=NEW."CatalogFileId" FOR NO KEY UPDATE;
        IF NOT EXISTS (SELECT 1 FROM romd."CurrentCatalogCdLayoutBindings" binding
            WHERE binding."CatalogFileId"=NEW."CatalogFileId" AND binding."Id"=NEW."CatalogBindingId" AND binding."Revision"=NEW."LayoutRevision" AND
                binding."CatalogReleaseId"=NEW."ReleaseId" AND binding."PlatformId"=NEW."PlatformId" AND
                binding."CatalogFingerprint"=NEW."CatalogFingerprint" AND binding."Path"=NEW."EntryPoint") THEN
            RAISE EXCEPTION 'Attempt catalog CD source changed before capture' USING ERRCODE='23514', CONSTRAINT='media_reconciliation_source_changed';
        END IF;
    END IF;
    IF NEW."DecoderRid" IS NOT NULL AND NEW."DecoderBuild" <> 'libchdr/' || NEW."DecoderRid" || '/pin:' || encode(NEW."DecoderUpstreamCommit",'hex') || '/recipe:' || NEW."DecoderRecipeVersion"::text THEN
        RAISE EXCEPTION 'Captured decoder label contradicts its exact native identity' USING ERRCODE = '23514';
    END IF;
    IF cardinality(NEW."LogicalInspectionIds") <> (SELECT count(DISTINCT id) FROM unnest(NEW."LogicalInspectionIds") id) THEN
        RAISE EXCEPTION 'Captured logical observation identities must be distinct' USING ERRCODE = '23514';
    END IF;
    IF NEW."Outcome" = 'Recorded' THEN
        SELECT * INTO proof FROM romd."Verifications" WHERE "Id" = NEW."ResultVerificationId" AND "RepresentationId" = NEW."ResultRepresentationId";
        IF NOT FOUND OR proof."DecoderBuild" <> NEW."DecoderBuild" OR
            (proof."DecoderRid",proof."DecoderUpstreamCommit",proof."DecoderRecipeVersion") IS DISTINCT FROM
            (NEW."DecoderRid",NEW."DecoderUpstreamCommit",NEW."DecoderRecipeVersion") THEN
            RAISE EXCEPTION 'Recorded work requires its complete exact captured decoder measurement' USING ERRCODE = '23514';
        END IF;
        IF NEW."Operation" = 'Inspect' AND NOT EXISTS (SELECT 1 FROM romd."LogicalInspections" observation
            JOIN romd."MediaReconciliationInputs" i ON i."OutputId" = observation."SourceOutputId" AND i."RomId" = observation."RomId"
            WHERE observation."Id" = NEW."ResultInspectionId" AND observation."SourceVerificationId" = NEW."ResultVerificationId" AND
                i."JobId" = NEW."JobId" AND i."AttemptNumber" = NEW."Number" AND i."RepresentationId" = NEW."ResultRepresentationId") THEN
            RAISE EXCEPTION 'Inspection completion must bind its actual measured logical source and immutable raw observation' USING ERRCODE = '23514';
        END IF;
        IF NEW."Operation" IN ('Verify','Release') AND NOT EXISTS (SELECT 1 FROM romd."MediaReconciliationInputs" i WHERE i."JobId" = NEW."JobId" AND
            i."AttemptNumber" = NEW."Number" AND i."RepresentationId" = NEW."ResultRepresentationId" AND i."ArtifactSha256" = proof."ArtifactSha256" AND
            i."ArtifactSize" = proof."ArtifactSize" AND (NEW."Operation" <> 'Release' OR i."VerificationId" = NEW."ResultVerificationId")) THEN
            RAISE EXCEPTION 'Verification and release must bind a captured exact artifact and canonical proof' USING ERRCODE = '23514';
        END IF;
        IF NEW."Operation" = 'Adopt' THEN
            IF proof."CheckedProfile" <> 'chd-cd-whole-files/v1' OR NOT EXISTS (
                SELECT 1 FROM romd."MediaReconciliationInputs" i JOIN romd."Representations" target ON target."Id"=NEW."ResultRepresentationId"
                WHERE i."JobId"=NEW."JobId" AND i."AttemptNumber"=NEW."Number" AND i."WitnessBindingId" IS NOT NULL AND
                    i."FileId"=target."FileId" AND i."ArtifactSha256"=proof."ArtifactSha256" AND i."ArtifactSize"=proof."ArtifactSize") OR NOT EXISTS (
                SELECT 1 FROM romd."MediaReconciliationJobs" job JOIN romd."CdArchiveOwners" owner ON owner."LayoutId"=job."SubjectId"
                WHERE job."JobId"=NEW."JobId" AND job."SubjectKind"='OriginalLayout' AND owner."RepresentationId"=NEW."ResultRepresentationId" AND
                    owner."VerificationId"=NEW."ResultVerificationId") OR
                (SELECT count(*) FROM romd."ReconstructionMembers" WHERE "RepresentationId"=NEW."ResultRepresentationId") <>
                (SELECT count(*) FROM romd."MediaReconciliationInputs" WHERE "JobId"=NEW."JobId" AND "AttemptNumber"=NEW."Number" AND "Role" IN ('Bin','Cue','Sidecar')) OR
                EXISTS (SELECT 1 FROM romd."MediaReconciliationInputs" i WHERE i."JobId"=NEW."JobId" AND i."AttemptNumber"=NEW."Number" AND
                    i."Role" IN ('Bin','Cue','Sidecar') AND NOT EXISTS (
                        SELECT 1 FROM romd."ReconstructionMembers" member WHERE member."RepresentationId"=NEW."ResultRepresentationId" AND member."Path"=i."Path" AND
                            (member."ContentSha256",member."ContentSize",member."Sha1",member."Md5",member."Crc32") =
                            (i."ContentSha256",i."ContentSize",i."Sha1",i."Md5",i."Crc32") AND
                            ((i."Role"='Bin' AND member."StoredFileId" IS NULL AND EXISTS (
                                SELECT 1 FROM romd."RepresentationOutputs" output WHERE output."RepresentationId"=member."RepresentationId" AND
                                    output."MemberOrdinal"=member."Ordinal" AND output."RomId"=i."RomId")) OR
                             (i."Role" IN ('Cue','Sidecar') AND member."StoredRole"=i."Role" AND member."StoredFileId"=i."FileId")))) THEN
                RAISE EXCEPTION 'Adoption completion requires its witnessed retained bytes, exact compared originals and actual verification-backed owner' USING ERRCODE='23514';
            END IF;
        END IF;
        IF NEW."Operation" = 'Encode' THEN
            SELECT count(*) INTO source_count FROM romd."MediaReconciliationInputs" i WHERE i."JobId" = NEW."JobId" AND i."AttemptNumber" = NEW."Number" AND i."Role" IN ('Whole','Bin');
            IF source_count = 0 OR NOT EXISTS (SELECT 1 FROM romd."RepresentationCreations" c WHERE c."Id" = NEW."ResultCreationId" AND
                    c."RepresentationId" = NEW."ResultRepresentationId" AND c."VerificationId" = NEW."ResultVerificationId" AND c."RecipeId" = NEW."RecipeId" AND c."EncoderBuild" = NEW."EncoderBuild") OR
                source_count <> (SELECT count(*) FROM romd."CreationSources" c WHERE c."CreationId" = NEW."ResultCreationId") OR
                EXISTS (SELECT 1 FROM romd."CreationSources" c JOIN romd."RepresentationOutputs" target ON target."Id" = c."TargetOutputId"
                    LEFT JOIN romd."ReconstructionMembers" member ON member."RepresentationId" = target."RepresentationId" AND member."Ordinal" = target."MemberOrdinal"
                    WHERE c."CreationId" = NEW."ResultCreationId" AND NOT EXISTS (
                    SELECT 1 FROM romd."MediaReconciliationInputs" i WHERE i."JobId" = NEW."JobId" AND i."AttemptNumber" = NEW."Number" AND i."Role" IN ('Whole','Bin') AND
                        (i."RepresentationId",i."OutputId",i."VerificationId",i."FileId",i."ArtifactSha256",i."ArtifactSize") =
                        (c."SourceRepresentationId",c."SourceOutputId",c."SourceVerificationId",c."SourceFileId",c."SourceArtifactSha256",c."SourceArtifactSize") AND
                        ((i."Role" = 'Whole' AND target."MemberOrdinal" IS NULL) OR (i."Role" = 'Bin' AND member."Path" = i."Path")))) THEN
                RAISE EXCEPTION 'Encoding completion requires the actual recipe/encoder creation and complete compared source vector' USING ERRCODE = '23514';
            END IF;
        END IF;
    END IF;
    RETURN NEW;
END; $body$;
CREATE TRIGGER "TR_MediaReconciliationAttempts_Identity" BEFORE INSERT OR UPDATE OR DELETE ON romd."MediaReconciliationAttempts"
    FOR EACH ROW EXECUTE FUNCTION romd.guard_media_reconciliation_attempt();

CREATE FUNCTION romd.guard_media_reconciliation_input() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path = '' AS $body$
DECLARE attempt record; scope record; retained record;
BEGIN
    IF TG_OP <> 'INSERT' THEN RAISE EXCEPTION 'Captured input observations are immutable' USING ERRCODE = '23514'; END IF;
    SELECT * INTO attempt FROM romd."MediaReconciliationAttempts" WHERE "JobId" = NEW."JobId" AND "Number" = NEW."AttemptNumber";
    IF NOT FOUND OR attempt."Outcome" IS NOT NULL OR NEW."Ordinal" >= attempt."SourceCount" THEN
        RAISE EXCEPTION 'An input requires a bounded active attempt' USING ERRCODE = '23514';
    END IF;
    PERFORM romd.require_media_reconciliation_fence(NEW."JobId",attempt."ExecutionFenceToken");
    SELECT * INTO scope FROM romd."MediaReconciliationJobs" WHERE "JobId" = NEW."JobId";
    IF NEW."WitnessBindingId" IS NOT NULL AND (attempt."Operation" <> 'Adopt' OR scope."SubjectKind" <> 'OriginalLayout' OR NOT EXISTS (
        SELECT 1 FROM romd."CurrentCatalogCdLayoutBindings" binding WHERE binding."Id" = NEW."WitnessBindingId" AND
            binding."CatalogFileId" = NEW."WitnessCatalogFileId" AND binding."Revision" = NEW."WitnessRevision" AND
            binding."RepresentationId" = NEW."RepresentationId" AND binding."PlatformId" = attempt."PlatformId" AND
            binding."CatalogReleaseId" = attempt."ReleaseId" AND binding."CatalogFingerprint" = attempt."CatalogFingerprint")) THEN
        RAISE EXCEPTION 'Adoption input requires its exact complete current catalog binding' USING ERRCODE='23514', CONSTRAINT='media_reconciliation_source_changed';
    END IF;
    IF NEW."Role" IN ('Cue','Sidecar') THEN
        PERFORM 1 FROM romd."Files" WHERE "Id" = NEW."FileId" FOR NO KEY UPDATE;
        IF NOT EXISTS (SELECT 1 FROM romd."Files" WHERE "Id"=NEW."FileId" AND "Sha256"=NEW."ArtifactSha256" AND "Size"=NEW."ArtifactSize") OR NOT (
            scope."SubjectKind"='OriginalLayout' AND attempt."OriginalLayoutId"=scope."SubjectId" AND (
                NEW."Role"='Cue' AND EXISTS (SELECT 1 FROM romd."OriginalDiscLayouts" layout WHERE layout."Id"=attempt."OriginalLayoutId" AND
                    layout."Revision"=attempt."LayoutRevision" AND layout."DescriptorFileId"=NEW."FileId" AND NEW."Path"=attempt."EntryPoint") OR
                NEW."Role"='Sidecar' AND EXISTS (SELECT 1 FROM romd."CdArchiveOwners" owner JOIN romd."ReconstructionMembers" member ON member."RepresentationId"=owner."RepresentationId"
                    WHERE owner."LayoutId"=attempt."OriginalLayoutId" AND member."StoredRole"='Sidecar' AND
                        (member."Path",member."StoredFileId",member."ContentSha256",member."ContentSize",member."Sha1",member."Md5",member."Crc32")=
                        (NEW."Path",NEW."FileId",NEW."ContentSha256",NEW."ContentSize",NEW."Sha1",NEW."Md5",NEW."Crc32"))) OR
            scope."SubjectKind"='CatalogCdLayout' AND NEW."Role"='Cue' AND attempt."CatalogFileId"=scope."SubjectId" AND EXISTS (
                SELECT 1 FROM romd."CurrentCatalogCdLayoutBindings" binding JOIN romd."ReconstructionMembers" member
                    ON member."RepresentationId"=binding."RepresentationId" AND member."Ordinal"=binding."MemberOrdinal"
                WHERE binding."CatalogFileId"=scope."SubjectId" AND binding."Id"=attempt."CatalogBindingId" AND binding."Revision"=attempt."LayoutRevision" AND
                    member."StoredRole"='Cue' AND
                    (member."Path",member."StoredFileId",member."ContentSha256",member."ContentSize",member."Sha1",member."Md5",member."Crc32")=
                    (NEW."Path",NEW."FileId",NEW."ContentSha256",NEW."ContentSize",NEW."Sha1",NEW."Md5",NEW."Crc32"))) THEN
            RAISE EXCEPTION 'Descriptor capture requires its exact physical file and closed layout scope' USING ERRCODE='23514', CONSTRAINT='media_reconciliation_source_changed';
        END IF;
        RETURN NEW;
    END IF;
    -- Same global order as publication: logical identities, representations, then ledger files.
    PERFORM 1 FROM romd."Roms" WHERE "Id" IN (SELECT "RomId" FROM romd."RepresentationOutputs" WHERE "RepresentationId" = NEW."RepresentationId") ORDER BY "Id" FOR NO KEY UPDATE;
    PERFORM pg_advisory_xact_lock(9415,NEW."RepresentationId");
    PERFORM 1 FROM romd."Representations" WHERE "Id" = NEW."RepresentationId" FOR NO KEY UPDATE;
    PERFORM 1 FROM romd."Files" WHERE "Id" IN (SELECT "FileId" FROM romd."Representations" WHERE "Id" = NEW."RepresentationId"
        UNION SELECT "StoredFileId" FROM romd."ReconstructionMembers" WHERE "RepresentationId" = NEW."RepresentationId" AND "StoredFileId" IS NOT NULL) ORDER BY "Id" FOR NO KEY UPDATE;
    SELECT r."FileId",r."Encoding",f."Sha256",f."Size" INTO retained FROM romd."Representations" r JOIN romd."Files" f ON f."Id" = r."FileId" WHERE r."Id" = NEW."RepresentationId";
    IF NOT FOUND OR (retained."FileId",retained."Sha256",retained."Size",romd.media_parameter_fingerprint(NEW."RepresentationId")) IS DISTINCT FROM
            (NEW."FileId",NEW."ArtifactSha256",NEW."ArtifactSize",NEW."ParametersSha256") OR
        NEW."VerificationId" IS NOT NULL AND NOT EXISTS (SELECT 1 FROM romd."Verifications" WHERE "Id" = NEW."VerificationId" AND "RepresentationId" = NEW."RepresentationId") OR
        scope."SubjectKind" = 'LogicalRom' AND (NEW."RomId" IS NOT NULL AND NEW."RomId" <> scope."SubjectId" OR
            NOT EXISTS (SELECT 1 FROM romd."RepresentationOutputs" WHERE "RepresentationId" = NEW."RepresentationId" AND "RomId" = scope."SubjectId")) OR
        scope."SubjectKind" = 'Representation' AND (NEW."RepresentationId" <> scope."SubjectId" OR NOT (
            EXISTS (SELECT 1 FROM romd."RepresentationOutputs" WHERE "RepresentationId" = NEW."RepresentationId" AND "RomId" IS NOT NULL) OR
            EXISTS (SELECT 1 FROM romd."CdArchiveOwners" WHERE "RepresentationId" = NEW."RepresentationId") OR
            EXISTS (SELECT 1 FROM romd."CatalogCdLayoutBindings" WHERE "RepresentationId" = NEW."RepresentationId") OR
            EXISTS (SELECT 1 FROM romd."OriginalDiscLayoutFiles" WHERE "FileId" = NEW."FileId"))) OR
        scope."SubjectKind" = 'OriginalLayout' AND NOT (
            EXISTS (SELECT 1 FROM romd."CdArchiveOwners" WHERE "LayoutId" = scope."SubjectId" AND "RepresentationId" = NEW."RepresentationId") OR
            EXISTS (SELECT 1 FROM romd."OriginalDiscLayoutFiles" l JOIN romd."RepresentationOutputs" o ON o."RomId" = l."RomId" WHERE l."OriginalDiscLayoutId" = scope."SubjectId" AND o."RepresentationId" = NEW."RepresentationId")) OR
        scope."SubjectKind"='CatalogCdLayout' AND NOT EXISTS (SELECT 1 FROM romd."CurrentCatalogCdLayoutBindings" binding
            WHERE binding."CatalogFileId"=scope."SubjectId" AND binding."Id"=attempt."CatalogBindingId" AND binding."Revision"=attempt."LayoutRevision" AND
                (binding."RepresentationId"=NEW."RepresentationId" OR EXISTS (
                    SELECT 1 FROM romd."RepresentationOutputs" source JOIN romd."RepresentationOutputs" candidate ON candidate."RomId"=source."RomId"
                    WHERE source."RepresentationId"=binding."RepresentationId" AND source."RomId" IS NOT NULL AND candidate."RepresentationId"=NEW."RepresentationId"))) THEN
        RAISE EXCEPTION 'Input capture requires the exact retained artifact, parameters, proof and subject scope' USING ERRCODE = '23514', CONSTRAINT = 'media_reconciliation_source_changed';
    END IF;
    IF scope."SubjectKind" = 'OriginalLayout' AND NEW."Role" IN ('Whole','Bin') AND NOT EXISTS (
        SELECT 1 FROM romd."OriginalDiscLayouts" layout JOIN romd."OriginalDiscLayoutFiles" member ON member."OriginalDiscLayoutId" = layout."Id"
        WHERE layout."Id" = attempt."OriginalLayoutId" AND layout."Revision" = attempt."LayoutRevision" AND member."RomId" = NEW."RomId" AND member."FileId" = NEW."FileId" AND
            (layout."State" = 'pending' AND member."BindingRevision" = '00000000-0000-0000-0000-000000000000' OR layout."State" = 'accepted' AND member."BindingRevision" = layout."Revision") AND
            replace(member."Path",chr(92),'/') = regexp_replace(replace(layout."EntryPoint",chr(92),'/'),'[^/]+$','') || NEW."Path") THEN
        RAISE EXCEPTION 'Logical input is no longer this exact current original layout member' USING ERRCODE = '23514', CONSTRAINT = 'media_reconciliation_source_changed';
    END IF;
    IF scope."SubjectKind"='CatalogCdLayout' AND NEW."Role" IN ('Whole','Bin') AND (NEW."Role"<>'Bin' OR NOT EXISTS (
        SELECT 1 FROM romd."CurrentCatalogCdLayoutBindings" binding JOIN romd."RepresentationOutputs" output ON output."RepresentationId"=binding."RepresentationId"
        JOIN romd."ReconstructionMembers" member ON member."RepresentationId"=output."RepresentationId" AND member."Ordinal"=output."MemberOrdinal"
        WHERE binding."CatalogFileId"=scope."SubjectId" AND binding."Id"=attempt."CatalogBindingId" AND binding."Revision"=attempt."LayoutRevision" AND
            (output."RomId",member."Path",output."ContentSha256",output."ContentSize",output."Sha1",output."Md5",output."Crc32")=
            (NEW."RomId",NEW."Path",NEW."ContentSha256",NEW."ContentSize",NEW."Sha1",NEW."Md5",NEW."Crc32"))) THEN
        RAISE EXCEPTION 'Logical input is no longer this exact catalog CD member' USING ERRCODE='23514', CONSTRAINT='media_reconciliation_source_changed';
    END IF;
    IF NEW."Role" IN ('Whole','Bin') AND NOT EXISTS (SELECT 1 FROM romd."RepresentationOutputs" o
        WHERE o."Id" = NEW."OutputId" AND o."RepresentationId" = NEW."RepresentationId" AND
            (o."RomId",o."ContentSha256",o."ContentSize",o."Sha1",o."Md5",o."Crc32") =
            (NEW."RomId",NEW."ContentSha256",NEW."ContentSize",NEW."Sha1",NEW."Md5",NEW."Crc32") AND
            (NEW."Role" <> 'Whole' OR o."MemberOrdinal" IS NULL)) THEN
        RAISE EXCEPTION 'Logical input capture requires its exact owned output and full identity' USING ERRCODE = '23514', CONSTRAINT = 'media_reconciliation_source_changed';
    END IF;
    RETURN NEW;
END; $body$;
CREATE TRIGGER "TR_MediaReconciliationInputs_Identity" BEFORE INSERT OR UPDATE OR DELETE ON romd."MediaReconciliationInputs"
    FOR EACH ROW EXECUTE FUNCTION romd.guard_media_reconciliation_input();

CREATE FUNCTION romd.guard_media_reconciliation_pin() RETURNS trigger LANGUAGE plpgsql SET search_path = '' AS $body$
DECLARE attempt record;
BEGIN
    IF TG_OP = 'UPDATE' THEN RAISE EXCEPTION 'Active source pins are immutable' USING ERRCODE = '23514'; END IF;
    SELECT * INTO attempt FROM romd."MediaReconciliationAttempts" WHERE "JobId" = coalesce(NEW."JobId",OLD."JobId") AND "Number" = coalesce(NEW."AttemptNumber",OLD."AttemptNumber");
    IF TG_OP = 'DELETE' THEN
        IF attempt."Outcome" IS NULL OR attempt."StoppedAtUtcTicks" IS NULL THEN
            RAISE EXCEPTION 'Source release requires a stopped durable attempt outcome' USING ERRCODE = '23514';
        END IF;
        RETURN OLD;
    END IF;
    PERFORM romd.require_media_reconciliation_fence(NEW."JobId",attempt."ExecutionFenceToken");
    IF attempt."Outcome" IS NOT NULL OR NOT EXISTS (SELECT 1 FROM romd."MediaReconciliationInputs" i WHERE
        (i."JobId",i."AttemptNumber",i."Ordinal",i."FileId") = (NEW."JobId",NEW."AttemptNumber",NEW."Ordinal",NEW."FileId") AND
        (i."RepresentationId",i."OutputId",i."VerificationId") IS NOT DISTINCT FROM (NEW."RepresentationId",NEW."OutputId",NEW."VerificationId")) THEN
        RAISE EXCEPTION 'Active source pin must match its exact captured input' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END; $body$;
CREATE TRIGGER "TR_MediaReconciliationPins_Custody" BEFORE INSERT OR UPDATE OR DELETE ON romd."MediaReconciliationPins"
    FOR EACH ROW EXECUTE FUNCTION romd.guard_media_reconciliation_pin();

CREATE FUNCTION romd.guard_media_reconciliation_failure() RETURNS trigger LANGUAGE plpgsql SET search_path = '' AS $body$
BEGIN
    IF TG_OP <> 'INSERT' OR NOT EXISTS (SELECT 1 FROM romd."MediaReconciliationAttempts" a WHERE a."JobId" = NEW."JobId" AND a."Number" = NEW."AttemptNumber" AND
        a."Outcome" = 'Failed' AND a."Deterministic" AND a."Fingerprint" = NEW."Fingerprint") THEN
        RAISE EXCEPTION 'Cached failure must retain its exact immutable deterministic attempt' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END; $body$;
CREATE TRIGGER "TR_MediaReconciliationFailures_Identity" BEFORE INSERT OR UPDATE OR DELETE ON romd."MediaReconciliationFailures"
    FOR EACH ROW EXECUTE FUNCTION romd.guard_media_reconciliation_failure();

CREATE FUNCTION romd.guard_media_reconciliation_outcome() RETURNS trigger LANGUAGE plpgsql SET search_path = '' AS $body$
DECLARE definition record;
BEGIN
    IF TG_OP <> 'INSERT' THEN RAISE EXCEPTION 'Reconciliation outcomes are immutable' USING ERRCODE = '23514'; END IF;
    PERFORM romd.require_media_reconciliation_fence(NEW."JobId",NEW."ExecutionFenceToken");
    SELECT * INTO definition FROM romd."MediaReconciliationJobs" WHERE "JobId" = NEW."JobId";
    IF definition."RequestMode" = 'Subject' AND (NEW."Kind" = 'CompletedWithErrors' OR NOT EXISTS (SELECT 1 FROM romd."MediaReconciliationSubjects" s
            WHERE s."LatestJobId" = NEW."JobId" AND s."RequestedVersion" = NEW."AcknowledgedVersion")) OR
        definition."RequestMode" = 'Selection' AND NEW."AcknowledgedVersion" <> 0 THEN
        RAISE EXCEPTION 'A terminal outcome must settle the exact current request version' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END; $body$;
CREATE TRIGGER "TR_MediaReconciliationOutcomes_Identity" BEFORE INSERT OR UPDATE OR DELETE ON romd."MediaReconciliationOutcomes"
    FOR EACH ROW EXECUTE FUNCTION romd.guard_media_reconciliation_outcome();

CREATE FUNCTION romd.guard_media_reconciliation_selection_item() RETURNS trigger LANGUAGE plpgsql SET search_path = '' AS $body$
DECLARE definition record;
BEGIN
    IF TG_OP <> 'INSERT' THEN RAISE EXCEPTION 'Selection and original child ownership are immutable' USING ERRCODE = '23514'; END IF;
    SELECT d.*,r."CompletedAt",r."ExecutionFenceToken" INTO definition FROM romd."MediaReconciliationJobs" d JOIN romd."Jobs" r ON r."Id" = d."JobId" WHERE d."JobId" = NEW."SelectionJobId";
    PERFORM romd.require_media_reconciliation_fence(NEW."SelectionJobId",definition."ExecutionFenceToken");
    IF definition."RequestMode" <> 'Selection' OR definition."CancelRequested" OR
        NEW."SubjectId" > (CASE NEW."SubjectKind" WHEN 'LogicalRom' THEN definition."UpperRomId" WHEN 'OriginalLayout' THEN definition."UpperLayoutId" WHEN 'Representation' THEN definition."UpperRepresentationId" WHEN 'CatalogCdLayout' THEN definition."UpperCatalogFileId" END) OR
        definition."Filter" = 'RetainedRepresentations' AND NEW."SubjectKind" <> 'Representation' OR
        NEW."ChildJobId" IS NOT NULL AND NOT EXISTS (SELECT 1 FROM romd."MediaReconciliationJobs" child WHERE child."JobId" = NEW."ChildJobId" AND child."RequestMode" = 'Subject' AND
            child."SubjectKind" = NEW."SubjectKind" AND child."SubjectId" = NEW."SubjectId" AND (NOT NEW."IsOwned" OR child."OriginSelectionJobId" = NEW."SelectionJobId")) THEN
        RAISE EXCEPTION 'Selection must bind its captured bounds, exact subject and original child ownership' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END; $body$;
CREATE TRIGGER "TR_MediaReconciliationSelectionItems_Identity" BEFORE INSERT OR UPDATE OR DELETE ON romd."MediaReconciliationSelectionItems"
    FOR EACH ROW EXECUTE FUNCTION romd.guard_media_reconciliation_selection_item();

CREATE FUNCTION romd.validate_media_reconciliation_job(job_id uuid) RETURNS void LANGUAGE plpgsql SET search_path = '' AS $body$
DECLARE root record; definition record; outcome record; attempt record; input_count integer; pin_count integer; max_ordinal integer;
    selected_count bigint; created_count bigint; adopted_count bigint; skipped_count bigint; completed_count bigint; failed_count bigint;
BEGIN
    SELECT * INTO root FROM romd."Jobs" WHERE "Id" = job_id;
    IF NOT FOUND THEN RETURN; END IF;
    SELECT * INTO definition FROM romd."MediaReconciliationJobs" WHERE "JobId" = job_id;
    IF root."JobType" <> 'media-reconciliation' THEN
        IF definition."JobId" IS NOT NULL THEN RAISE EXCEPTION 'Reconciliation definition cannot belong to another family' USING ERRCODE = '23514'; END IF;
        RETURN;
    END IF;
    IF definition."JobId" IS NULL THEN RAISE EXCEPTION 'Reconciliation jobs require an exact typed definition' USING ERRCODE = '23514'; END IF;
    IF NOT EXISTS (SELECT 1 FROM romd."MediaReconciliationRequests" WHERE "JobId" = job_id) THEN
        RAISE EXCEPTION 'Reconciliation requires its immutable accepted request attribution' USING ERRCODE = '23514';
    END IF;
    SELECT * INTO outcome FROM romd."MediaReconciliationOutcomes" WHERE "JobId" = job_id;
    IF definition."RequestMode" = 'Subject' AND EXISTS (SELECT 1 FROM romd."MediaReconciliationRequests" request WHERE request."JobId" = job_id AND
        NOT EXISTS (SELECT 1 FROM romd."MediaReconciliationSubjects" subject WHERE subject."SubjectKind" = definition."SubjectKind" AND subject."SubjectId" = definition."SubjectId" AND
            request."RequestedVersion" > 0 AND request."RequestedVersion" <= subject."RequestedVersion" AND
            (root."CompletedAt" IS NOT NULL AND request."RequestedVersion" <= outcome."AcknowledgedVersion" OR
             root."CompletedAt" IS NULL AND subject."LatestJobId" = job_id AND (NOT request."ConversionRequested" OR subject."ConversionRequested")))) THEN
        RAISE EXCEPTION 'Every accepted request must bind the exact durable subject demand and retained conversion intent' USING ERRCODE = '23514';
    END IF;
    IF definition."OriginSelectionJobId" IS NOT NULL AND NOT EXISTS (SELECT 1 FROM romd."MediaReconciliationSelectionItems" item
        WHERE item."SelectionJobId" = definition."OriginSelectionJobId" AND item."ChildJobId" = job_id AND item."IsOwned" AND
            item."SubjectKind" = definition."SubjectKind" AND item."SubjectId" = definition."SubjectId") THEN
        RAISE EXCEPTION 'Original selection ownership requires its atomic exact selection item' USING ERRCODE = '23514';
    END IF;
    IF root."CompletedAt" IS NULL THEN
        IF outcome."JobId" IS NOT NULL OR
            definition."RequestMode" = 'Subject' AND (root."Phase" NOT IN ('Pending','Planning','Inspecting','Verifying','Encoding','Releasing','Publishing') OR
                NOT EXISTS (SELECT 1 FROM romd."MediaReconciliationSubjects" s WHERE s."LatestJobId" = job_id AND s."AcknowledgedVersion" < s."RequestedVersion")) OR
            definition."RequestMode" = 'Selection' AND root."Phase" NOT IN ('Pending','Selecting','Waiting') THEN
            RAISE EXCEPTION 'Active reconciliation phase must retain its unsettled scope and have no terminal journal' USING ERRCODE = '23514';
        END IF;
    ELSE
        IF outcome."JobId" IS NULL OR root."Phase" <> outcome."Kind" OR
            root."CompletedAt" <> (outcome."CompletedAtUtcTicks" - 621355968000000000) / 10000 OR
            EXISTS (SELECT 1 FROM romd."MediaReconciliationPins" WHERE "JobId" = job_id) OR
            EXISTS (SELECT 1 FROM romd."MediaReconciliationAttempts" WHERE "JobId" = job_id AND "Outcome" IS NULL) OR
            definition."RequestMode" = 'Subject' AND EXISTS (SELECT 1 FROM romd."MediaReconciliationSubjects" s WHERE s."LatestJobId" = job_id AND
                (s."AcknowledgedVersion" <> outcome."AcknowledgedVersion" OR s."RequestedVersion" <> outcome."AcknowledgedVersion")) THEN
            RAISE EXCEPTION 'Terminal reconciliation must match its journal, current acknowledgement and complete source release' USING ERRCODE = '23514';
        END IF;
    END IF;
    FOR attempt IN SELECT * FROM romd."MediaReconciliationAttempts" WHERE "JobId" = job_id LOOP
        SELECT count(*),max("Ordinal") INTO input_count,max_ordinal FROM romd."MediaReconciliationInputs" WHERE "JobId" = job_id AND "AttemptNumber" = attempt."Number";
        SELECT count(*) INTO pin_count FROM romd."MediaReconciliationPins" WHERE "JobId" = job_id AND "AttemptNumber" = attempt."Number";
        IF (SELECT count(*) FROM romd."MediaReconciliationInputs" WHERE "JobId"=job_id AND "AttemptNumber"=attempt."Number" AND "WitnessBindingId" IS NOT NULL)
                <> (CASE WHEN attempt."Operation"='Adopt' THEN 1 ELSE 0 END) OR
            attempt."Operation"='Adopt' AND ((SELECT count(*) FROM romd."MediaReconciliationInputs" WHERE "JobId"=job_id AND "AttemptNumber"=attempt."Number" AND "Role"='Cue') <> 1 OR
                NOT EXISTS (SELECT 1 FROM romd."MediaReconciliationInputs" WHERE "JobId"=job_id AND "AttemptNumber"=attempt."Number" AND "Role"='Bin')) OR
            input_count <> attempt."SourceCount" OR max_ordinal <> attempt."SourceCount" - 1 OR
            (attempt."Outcome" IS NULL AND pin_count <> input_count) OR (attempt."Outcome" IS NOT NULL AND pin_count <> 0) THEN
            RAISE EXCEPTION 'Attempt capture and source release must cover the entire immutable input vector atomically' USING ERRCODE = '23514';
        END IF;
    END LOOP;
    IF definition."RequestMode" = 'Selection' THEN
        SELECT count(*),count(*) FILTER (WHERE "ChildJobId" IS NOT NULL AND "IsOwned"),count(*) FILTER (WHERE "ChildJobId" IS NOT NULL AND NOT "IsOwned"),
            count(*) FILTER (WHERE "ChildJobId" IS NULL) INTO selected_count,created_count,adopted_count,skipped_count
            FROM romd."MediaReconciliationSelectionItems" WHERE "SelectionJobId" = job_id;
        SELECT count(*) FILTER (WHERE child."CompletedAt" IS NOT NULL),count(*) FILTER (WHERE child."CompletedAt" IS NOT NULL AND child."Phase" IN ('Failed','Cancelled'))
            INTO completed_count,failed_count FROM romd."MediaReconciliationSelectionItems" item
            JOIN romd."Jobs" child ON child."Id" = item."ChildJobId"
            WHERE item."SelectionJobId" = job_id AND item."ChildJobId" IS NOT NULL;
        IF root."CompletedAt" IS NOT NULL AND (definition."CompletedCount",definition."FailedCount") IS DISTINCT FROM (completed_count,failed_count) OR
            (definition."SelectedCount",definition."CreatedCount",definition."AdoptedCount",definition."SkippedCount") IS DISTINCT FROM
            (selected_count,created_count,adopted_count,skipped_count) OR
            root."CompletedAt" IS NOT NULL AND EXISTS (SELECT 1 FROM romd."MediaReconciliationJobs" child JOIN romd."Jobs" c ON c."Id" = child."JobId"
                WHERE child."OriginSelectionJobId" = job_id AND c."CompletedAt" IS NULL) OR
            outcome."Kind" IN ('Completed','CompletedWithErrors') AND (NOT definition."EnumerationComplete" OR
                definition."CompletedCount" + definition."SkippedCount" <> definition."SelectedCount" OR
                (outcome."Kind" = 'CompletedWithErrors') IS DISTINCT FROM (definition."FailedCount" > 0) OR
                EXISTS (SELECT 1 FROM romd."MediaReconciliationSelectionItems" item JOIN romd."Jobs" child ON child."Id" = item."ChildJobId"
                    WHERE item."SelectionJobId" = job_id AND child."CompletedAt" IS NULL)) THEN
            RAISE EXCEPTION 'Selection progress, terminal counts and original child custody must match durable selection' USING ERRCODE = '23514';
        END IF;
    END IF;
END; $body$;

CREATE FUNCTION romd.guard_media_reconciliation_commit() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path = '' AS $body$
DECLARE job_id uuid; row_data jsonb;
BEGIN
    row_data := CASE WHEN TG_OP = 'DELETE' THEN to_jsonb(OLD) ELSE to_jsonb(NEW) END;
    job_id := (CASE TG_TABLE_NAME WHEN 'Jobs' THEN row_data ->> 'Id' WHEN 'MediaReconciliationSubjects' THEN row_data ->> 'LatestJobId'
        WHEN 'MediaReconciliationSelectionItems' THEN row_data ->> 'SelectionJobId' ELSE row_data ->> 'JobId' END)::uuid;
    PERFORM romd.validate_media_reconciliation_job(job_id);
    RETURN NULL;
END; $body$;
CREATE CONSTRAINT TRIGGER "TR_MediaReconciliationRoot_Commit" AFTER INSERT OR UPDATE ON romd."Jobs"
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION romd.guard_media_reconciliation_commit();
DO $body$
DECLARE table_name text;
BEGIN
    FOREACH table_name IN ARRAY ARRAY['MediaReconciliationJobs','MediaReconciliationSubjects','MediaReconciliationRequests','MediaReconciliationAttempts','MediaReconciliationInputs','MediaReconciliationPins','MediaReconciliationOutcomes','MediaReconciliationSelectionItems'] LOOP
        EXECUTE format('CREATE CONSTRAINT TRIGGER "TR_%s_Commit" AFTER INSERT OR UPDATE OR DELETE ON romd.%I DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION romd.guard_media_reconciliation_commit()',table_name,table_name);
    END LOOP;
END; $body$;
REVOKE ALL ON FUNCTION romd.require_media_reconciliation_fence(uuid,uuid),romd.guard_media_reconciliation_input(),romd.validate_media_reconciliation_job(uuid) FROM PUBLIC;
