-- Current non-media functions and checksum/reference invariants retained by the fresh baseline.
CREATE OR REPLACE FUNCTION romd.provider_identity_fingerprint(title_id integer, policy_version text)
 RETURNS text
 LANGUAGE sql
 STABLE
AS $function$
  SELECT encode(sha256(convert_to(jsonb_build_object(
    'Encoding', 'identity-db-1', 'Policy', policy_version,
    'Name', t."Name", 'Platform', jsonb_build_array(p."Name", p."ShortName"),
    'Local', COALESCE((
      SELECT jsonb_agg(jsonb_build_array(e.name, e.year) ORDER BY e.name COLLATE "C", e.year COLLATE "C")
      FROM (
        SELECT DISTINCT g."Name" AS name, g."Year" AS year
        FROM romd."TitleSourceLinks" l
        JOIN romd."SourceEntries" s ON s."Id" = l."SourceEntryId"
        JOIN romd."CatalogSources" c ON c."Id" = s."CatalogSourceId" AND c."Status" = 'Active'
        JOIN romd."DatGames" g ON g."SourceEntryId" = l."SourceEntryId"
        WHERE l."TitleId" = t."Id" AND NOT g."IsBios"
      ) e
    ), '[]'::jsonb),
    'Confirmed', COALESCE((
      SELECT jsonb_agg(jsonb_build_array(e."Provider", e."ExternalId") ORDER BY e."Provider" COLLATE "C")
      FROM romd."TitleExternalIds" e WHERE e."TitleId" = t."Id" AND e."IsConfirmed"
    ), '[]'::jsonb)
  )::text, 'UTF8')), 'hex')
  FROM romd."Titles" t JOIN romd."Platforms" p ON p."Id" = t."PlatformId"
  WHERE t."Id" = title_id;
$function$;

CREATE OR REPLACE FUNCTION romd.provider_identity_trusted_walk(title_id integer, provider_id text, policy_version text, visited text[], current_fingerprint text)
 RETURNS boolean
 LANGUAGE plpgsql
 STABLE
AS $function$
DECLARE
  identity_link record;
  identity_state record;
  decision jsonb;
  dependency jsonb;
  dependency_revision uuid;
BEGIN
  IF provider_id = ANY(visited) THEN RETURN false; END IF;
  SELECT * INTO identity_link FROM romd."TitleExternalIds"
    WHERE "TitleId" = title_id AND "Provider" = provider_id;
  IF NOT FOUND THEN RETURN false; END IF;
  SELECT * INTO identity_state FROM romd."TitleProviderMatchStates"
    WHERE "TitleId" = title_id AND "ProviderId" = provider_id;
  IF identity_state."SuppressAutomaticMatch" IS TRUE THEN RETURN false; END IF;
  IF identity_link."IsConfirmed" THEN RETURN true; END IF;
  IF identity_state."AutomaticallyTrusted" IS NOT TRUE
    OR identity_state."MatcherVersion" IS DISTINCT FROM policy_version
    OR identity_state."InputFingerprint" IS DISTINCT FROM current_fingerprint
    OR (identity_state."GameJson"::jsonb ->> 'Id') IS DISTINCT FROM identity_link."ExternalId"
    OR identity_state."DecisionJson" IS NULL THEN RETURN false; END IF;
  decision := identity_state."DecisionJson"::jsonb;
  IF jsonb_typeof(decision) IS DISTINCT FROM 'object' THEN RETURN false; END IF;
  IF decision -> 'Dependencies' IS NOT NULL AND decision -> 'Dependencies' <> 'null'::jsonb THEN
    IF jsonb_typeof(decision -> 'Dependencies') <> 'array' THEN RETURN false; END IF;
    FOR dependency IN SELECT value FROM jsonb_array_elements(decision -> 'Dependencies') LOOP
      IF NOT EXISTS (SELECT 1 FROM romd."TitleExternalIds" e
        WHERE e."TitleId" = title_id AND e."Provider" = dependency ->> 'ProviderId'
          AND e."ExternalId" = dependency ->> 'GameId') THEN RETURN false; END IF;
      SELECT COALESCE((SELECT s."Revision" FROM romd."TitleProviderMatchStates" s
        WHERE s."TitleId" = title_id AND s."ProviderId" = dependency ->> 'ProviderId'),
        '00000000-0000-0000-0000-000000000000'::uuid) INTO dependency_revision;
      IF dependency_revision IS DISTINCT FROM (dependency ->> 'Revision')::uuid
        OR NOT romd.provider_identity_trusted_walk(title_id, dependency ->> 'ProviderId', policy_version,
          array_append(visited, provider_id), current_fingerprint) THEN RETURN false; END IF;
    END LOOP;
  END IF;
  RETURN true;
END;
$function$;

CREATE OR REPLACE FUNCTION romd.provider_identity_trusted_walk(title_id integer, provider_id text, policy_version text, visited text[])
 RETURNS boolean
 LANGUAGE sql
 STABLE
AS $function$
  SELECT romd.provider_identity_trusted_walk(title_id, provider_id, policy_version, visited,
    romd.provider_identity_fingerprint(title_id, policy_version));
$function$;

CREATE OR REPLACE FUNCTION romd.provider_identity_trusted(title_id integer, provider_id text, policy_version text)
 RETURNS boolean
 LANGUAGE sql
 STABLE
AS $function$
  SELECT romd.provider_identity_trusted_walk(title_id, provider_id, policy_version, ARRAY[]::text[]);
$function$;

CREATE OR REPLACE FUNCTION romd.enrichment_requirement_facts_set(title_ids integer[], policy_version text, cover_enabled boolean, background_enabled boolean, logo_enabled boolean, enabled_providers text[], configured_providers text[], disabled_providers text[])
 RETURNS TABLE("TitleId" integer, "Kind" integer, "Subject" text, "Satisfied" boolean, "RequiresDecision" boolean, "Blocked" boolean, "Processing" boolean, "Cause" text, "SatisfiedBy" text)
 LANGUAGE sql
 STABLE
 SET search_path TO 'pg_catalog', 'romd'
 SET jit TO 'off'
AS $function$
WITH title AS MATERIALIZED (SELECT * FROM romd."Titles" WHERE "Id" = ANY(title_ids)),
fingerprints AS MATERIALIZED (
 SELECT t."Id", CASE WHEN EXISTS(SELECT 1 FROM romd."TitleProviderMatchStates" s WHERE s."TitleId"=t."Id" AND NOT s."SuppressAutomaticMatch" AND s."MatcherVersion"=policy_version)
 THEN romd.provider_identity_fingerprint(t."Id",policy_version) END AS fingerprint FROM title t
), identity AS MATERIALIZED (
 SELECT t."Id" AS title_id,p.provider,p.provider=ANY(configured_providers) AS available,
 coalesce(s."SuppressAutomaticMatch",false) AS suppressed,
 CASE WHEN coalesce(s."SuppressAutomaticMatch",false) THEN false WHEN e."IsConfirmed" THEN true
 WHEN e."Id" IS NULL THEN false WHEN s."AutomaticallyTrusted" AND s."InputFingerprint"=f.fingerprint AND s."MatcherVersion"=policy_version
 THEN romd.provider_identity_trusted_walk(t."Id",p.provider,policy_version,ARRAY[]::text[],f.fingerprint) ELSE false END AS trusted,
 coalesce(s."InputFingerprint"=f.fingerprint AND s."MatcherVersion"=policy_version,false) AS fresh,
 s."DecisionJson"::jsonb ->> 'State' AS decision
 FROM title t CROSS JOIN unnest(enabled_providers) p(provider)
 JOIN fingerprints f ON f."Id"=t."Id"
 LEFT JOIN romd."TitleProviderMatchStates" s ON s."TitleId"=t."Id" AND s."ProviderId"=p.provider
 LEFT JOIN romd."TitleExternalIds" e ON e."TitleId"=t."Id" AND e."Provider"=p.provider
), active AS MATERIALIZED (
 SELECT j."TitleId" AS title_id FROM romd."Jobs" j WHERE j."JobType"='enrichment' AND j."Phase" IN ('Pending','Enriching') AND j."TitleId"=ANY(title_ids)
 UNION SELECT i."TitleId" FROM romd."ArtworkCompletionItems" i JOIN romd."Jobs" j ON j."Id"=i."JobId"
 WHERE i."TitleId"=ANY(title_ids) AND i."CompletedAt" IS NULL AND j."Phase" IN ('Pending','Enriching')
 UNION SELECT t."Id" FROM title t JOIN romd."Jobs" j ON j."PlatformId"=t."PlatformId"
 WHERE j."JobType"='bulk_enrichment' AND j."Phase" IN ('Pending','Enriching') AND (j."Scope"='All' OR EXISTS(SELECT 1 FROM romd."TrackedTitles" tt WHERE tt."TitleId"=t."Id"))
 UNION SELECT item.value::integer FROM romd."Jobs" j
 CROSS JOIN LATERAL jsonb_array_elements_text(coalesce(j."ArtworkTitleIdsJson",'[]')::jsonb) item(value)
 WHERE j."JobType"='bulk_enrichment' AND j."Phase" IN ('Pending','Enriching') AND item.value::integer=ANY(title_ids)
), core AS MATERIALIZED (
 SELECT t."Id" AS title_id,
 (nullif(t."Description",'') IS NULL AND NOT EXISTS(SELECT 1 FROM romd."EnrichmentOmissions" o WHERE o."TitleId"=t."Id" AND o."Field"='Description'))
 OR (nullif(t."Genre",'') IS NULL AND NOT EXISTS(SELECT 1 FROM romd."EnrichmentOmissions" o WHERE o."TitleId"=t."Id" AND o."Field"='Genre'))
 OR (t."ReleaseDate" IS NULL AND NOT EXISTS(SELECT 1 FROM romd."EnrichmentOmissions" o WHERE o."TitleId"=t."Id" AND o."Field"='ReleaseDate')) AS missing
 FROM title t
), metadata AS MATERIALIZED (
 SELECT t."Id" AS title_id,coalesce(i.available AND NOT i.suppressed,false) AS available,
 coalesce(i.suppressed,false) AS suppressed,
 coalesce(NOT i.trusted AND NOT i.suppressed AND i.fresh AND i.decision IN ('NeedsReview','NoMatch','Unavailable'),false) AS waiting,
 coalesce(NOT i.trusted AND NOT i.suppressed AND i.fresh AND i.decision IN ('NeedsReview','NoMatch'),false) AS review_waiting,
 coalesce(i.available AND i.trusted,false) AND (c.missing OR EXISTS(SELECT 1 FROM romd."TitleMetadataLayers" l WHERE l."TitleId"=t."Id" AND l."SourceId"='igdb')) AND NOT EXISTS(
 SELECT 1 FROM romd."TitleMetadataLayers" l JOIN romd."TitleExternalIds" e ON e."TitleId"=l."TitleId" AND e."Provider"=l."SourceId"
 WHERE l."TitleId"=t."Id" AND l."SourceId"='igdb' AND l."MetadataJson"::jsonb ->> 'providerGameId'=e."ExternalId") AS stale,
 t."LastEnrichedAt" IS NOT NULL AND t."EnrichmentStatus" IN ('Completed','NotFound','LowConfidence') AS attempted,
 a.title_id IS NOT NULL AS processing
 FROM title t JOIN core c ON c.title_id=t."Id" LEFT JOIN identity i ON i.title_id=t."Id" AND i.provider='igdb'
 LEFT JOIN active a ON a.title_id=t."Id"
), roles AS MATERIALIZED (
 SELECT role,enabled,review,EXISTS(SELECT 1 FROM unnest(enabled_providers) e(provider)
 WHERE (r.role<>'Logo' OR e.provider='steamgriddb') AND (r.role<>'Backdrop' OR e.provider='igdb')) AS served FROM romd."ArtworkEnrichmentSettings" s
 CROSS JOIN LATERAL (VALUES ('Poster',s."FillPosters" AND cover_enabled,false),('Hero',s."FillHeroes" AND background_enabled,false),
 ('Logo',s."FillLogos" AND logo_enabled,false),('Backdrop',s."FillBackdrops" AND background_enabled,s."ReviewBackdrops")) r(role,enabled,review) WHERE s."Id"=1
), role_identity AS MATERIALIZED (
 SELECT i.title_id,r.role,bool_or(i.available AND NOT i.suppressed) AS available,
 bool_or(i.available AND i.trusted) AS trusted,
 bool_or(i.available) AND bool_and(NOT i.available OR i.suppressed) AS suppressed,
 bool_or(NOT i.trusted AND NOT i.suppressed AND i.fresh AND coalesce(i.decision IN ('NeedsReview','NoMatch','Unavailable'),false)) AS waiting,
 bool_or(NOT i.trusted AND NOT i.suppressed AND i.fresh AND coalesce(i.decision IN ('NeedsReview','NoMatch'),false)) AS review_waiting,
 bool_or(NOT i.trusted AND NOT i.suppressed AND i.fresh AND coalesce(i.decision='Unavailable',false)) AS unavailable_waiting
 FROM identity i CROSS JOIN roles r WHERE (r.role<>'Logo' OR i.provider='steamgriddb') AND (r.role<>'Backdrop' OR i.provider='igdb')
 GROUP BY i.title_id,r.role
), present AS MATERIALIZED (
 SELECT DISTINCT a."TitleId" AS title_id,a."Role" AS role FROM romd."ArtworkAssets" a WHERE a."TitleId"=ANY(title_ids) AND a."IsEligible" AND NOT a."IsRejected"
 UNION SELECT m."TitleId",'Poster' FROM romd."TitleMedia" m WHERE m."TitleId"=ANY(title_ids) AND m."Type"='Cover'
), artwork AS MATERIALIZED (
 SELECT t."Id" AS title_id,r.*,sel."Mode" AS mode,p.title_id IS NOT NULL AS present,
 j."Id" IS NOT NULL AS importing,coalesce(i.available,false) AS available,coalesce(i.trusted,false) AS trusted,
 coalesce(i.suppressed,false) AS suppressed,coalesce(i.waiting,false) AS waiting,coalesce(i.review_waiting,false) AS review_waiting,ac."Status" AS outcome,a.title_id IS NOT NULL AS processing,
 coalesce(i.unavailable_waiting,false) AS unavailable_waiting
 FROM title t CROSS JOIN roles r
 LEFT JOIN romd."ArtworkSelections" sel ON sel."TitleId"=t."Id" AND sel."Role"=r.role
 LEFT JOIN romd."ArtworkAcquisitions" ac ON ac."TitleId"=t."Id" AND ac."Role"=r.role
 LEFT JOIN present p ON p.title_id=t."Id" AND p.role=r.role
 LEFT JOIN role_identity i ON i.title_id=t."Id" AND i.role=r.role
 LEFT JOIN active a ON a.title_id=t."Id"
 LEFT JOIN romd."Jobs" j ON j."Id"=sel."PendingRequestId" AND j."JobType"='artwork-import' AND j."Phase" IN ('Pending','Importing')
), importing AS MATERIALIZED (SELECT DISTINCT title_id FROM artwork WHERE importing), demand AS MATERIALIZED (
 SELECT c.title_id,'igdb' AS provider FROM core c WHERE c.missing
 UNION SELECT a.title_id,i.provider FROM artwork a CROSS JOIN unnest(enabled_providers) i(provider)
 WHERE a.enabled AND NOT a.present AND a.mode IS DISTINCT FROM 'Empty' AND NOT (a.role='Backdrop' AND coalesce(a.outcome='Unavailable',false)) AND (a.role<>'Logo' OR i.provider='steamgriddb') AND (a.role<>'Backdrop' OR i.provider='igdb')
 UNION SELECT l."TitleId",l."SourceId" FROM romd."TitleMetadataLayers" l WHERE l."TitleId"=ANY(title_ids)
 UNION SELECT a."TitleId",a."SourceId" FROM romd."ArtworkAssets" a WHERE a."TitleId"=ANY(title_ids) AND a."IsEligible" AND NOT a."IsRejected"
), disabled_demand AS MATERIALIZED (
 SELECT c.title_id,'igdb' AS provider FROM core c WHERE c.missing AND 'igdb'=ANY(disabled_providers) AND NOT 'igdb'=ANY(enabled_providers)
 UNION SELECT a.title_id,p.provider FROM artwork a CROSS JOIN unnest(disabled_providers) p(provider)
 WHERE NOT a.served AND a.enabled AND NOT a.present AND a.mode IS DISTINCT FROM 'Empty' AND NOT (a.role IN ('Backdrop','Logo') AND coalesce(a.outcome='Unavailable',false)) AND (a.role<>'Logo' OR p.provider='steamgriddb') AND (a.role<>'Backdrop' OR p.provider='igdb')
)
-- Each arm supplies its classification facts, then the cause candidate for every state it can
-- reach. The outer selection applies EnrichmentAssessmentPolicy.Classify precedence.
SELECT fact.title_id,fact.kind,fact.subject,fact.satisfied,fact.requires_decision,fact.blocked,fact.processing,
 CASE WHEN fact.satisfied THEN NULL WHEN fact.requires_decision THEN fact.decision_cause WHEN fact.processing THEN fact.processing_cause
 WHEN fact.blocked THEN fact.blocked_cause ELSE fact.automatic_cause END,
 CASE WHEN fact.satisfied THEN fact.satisfied_by END
FROM (
SELECT i.title_id,0,i.provider,i.suppressed OR i.trusted,
 i.available AND NOT i.suppressed AND NOT i.trusted AND i.fresh AND coalesce(i.decision IN ('NeedsReview','NoMatch'),false),
 NOT i.available OR i.fresh AND coalesce(i.decision='Unavailable',false),a.title_id IS NOT NULL,
 CASE WHEN i.suppressed THEN 'ProviderSuppressed' WHEN i.trusted THEN 'Trusted' END,
 CASE i.decision WHEN 'NeedsReview' THEN 'MatchDecisionRequired' WHEN 'NoMatch' THEN 'NoMatchFound' END,'Working',
 CASE WHEN NOT i.available THEN 'ProviderUnconfigured' WHEN i.fresh AND i.decision='Unavailable' THEN 'ProviderUnavailable' END,'Discover'
FROM identity i JOIN demand d ON d.title_id=i.title_id AND d.provider=i.provider LEFT JOIN active a ON a.title_id=i.title_id
UNION ALL
-- A disabled provider is named only for requested work no enabled provider can serve. Without active
-- work or a pending import, its dependent metadata or artwork row is also Blocked. With either, this row
-- is Processing; a pending import alone can leave the dependent metadata row Blocked, but the Operation
-- row is then Processing, so no title flag changes.
SELECT d.title_id,0,d.provider,false,false,true,a.title_id IS NOT NULL OR imp.title_id IS NOT NULL,NULL,NULL,'Working','ProviderDisabled',NULL
FROM disabled_demand d LEFT JOIN active a ON a.title_id=d.title_id LEFT JOIN importing imp ON imp.title_id=d.title_id
UNION ALL
SELECT t."Id",1,f.field,f.present OR o.omitted,
 m.suppressed OR m.available AND NOT m.waiting AND m.attempted AND NOT m.stale,NOT m.suppressed AND (NOT m.available OR m.waiting),m.processing,
 CASE WHEN o.omitted THEN 'AcceptedOmission' WHEN f.present THEN 'Present' END,
 CASE WHEN m.suppressed THEN 'ProviderSuppressed' ELSE 'NotProvided' END,'Working',
 CASE WHEN NOT m.available THEN CASE WHEN 'igdb'=ANY(enabled_providers) THEN 'ProviderUnconfigured' ELSE 'ProviderDisabled' END
 WHEN m.waiting AND NOT m.review_waiting THEN 'ProviderUnavailable' WHEN m.waiting THEN 'AwaitingIdentity' END,
 CASE WHEN m.attempted AND m.stale THEN 'Stale' ELSE 'NotAttempted' END
FROM title t JOIN metadata m ON m.title_id=t."Id"
CROSS JOIN LATERAL (VALUES ('Description',nullif(t."Description",'') IS NOT NULL),('Genre',nullif(t."Genre",'') IS NOT NULL),('ReleaseDate',t."ReleaseDate" IS NOT NULL)) f(field,present)
CROSS JOIN LATERAL (SELECT EXISTS(SELECT 1 FROM romd."EnrichmentOmissions" omission WHERE omission."TitleId"=t."Id" AND omission."Field"=f.field) AS omitted) o
WHERE NOT m.review_waiting
UNION ALL SELECT m.title_id,1,'Refresh',NOT m.stale,false,false,m.processing,'Current',NULL,'Working',NULL,'Stale' FROM metadata m
UNION ALL SELECT a.title_id,2,a.role,NOT a.enabled OR a.present OR coalesce(a.mode='Empty',false) OR (a.role IN ('Backdrop','Logo') AND coalesce(a.outcome='Unavailable',false) AND NOT a.importing AND NOT a.processing),
 NOT a.importing AND (a.suppressed OR a.trusted AND (a.review OR NOT a.processing AND coalesce(a.outcome IN ('Unavailable','NeedsReview') AND (a.role<>'Backdrop' OR a.review),false))),
 NOT a.suppressed AND (NOT a.available OR NOT a.trusted AND a.waiting OR coalesce(a.outcome='Failed',false)),a.importing OR a.processing,
 CASE WHEN coalesce(a.mode='Empty',false) THEN 'LeftEmpty' WHEN a.present THEN 'Present' WHEN NOT a.enabled THEN 'NotRequested'
 WHEN a.role IN ('Backdrop','Logo') AND coalesce(a.outcome='Unavailable',false) AND NOT a.importing AND NOT a.processing THEN 'UnavailableFallback' END,
 CASE WHEN a.suppressed THEN 'ProviderSuppressed' WHEN coalesce(a.outcome='Unavailable',false) THEN 'NothingUsable'
 WHEN a.review OR coalesce(a.outcome='NeedsReview',false) THEN 'ArtworkDecisionRequired' END,
 CASE WHEN a.importing THEN 'Importing' WHEN a.processing THEN 'Searching' END,
 CASE WHEN NOT a.available THEN CASE WHEN a.served THEN 'ProviderUnconfigured' ELSE 'ProviderDisabled' END
 WHEN NOT a.trusted AND a.unavailable_waiting THEN 'ProviderUnavailable' WHEN NOT a.trusted AND a.review_waiting THEN 'AwaitingIdentity'
 WHEN coalesce(a.outcome='Failed',false) THEN 'AcquisitionFailed' END,'NotAttempted' FROM artwork a
 WHERE a.trusted OR NOT a.review_waiting
UNION ALL SELECT t."Id",3,'Processing',a.title_id IS NULL AND imp.title_id IS NULL,false,false,true,'Idle',NULL,'Working',NULL,NULL
FROM title t LEFT JOIN active a ON a.title_id=t."Id" LEFT JOIN importing imp ON imp.title_id=t."Id"
) fact(title_id,kind,subject,satisfied,requires_decision,blocked,processing,satisfied_by,decision_cause,processing_cause,blocked_cause,automatic_cause)
$function$;

CREATE OR REPLACE FUNCTION romd.guard_file_artifact_checksums()
 RETURNS trigger
 LANGUAGE plpgsql
AS $function$
BEGIN
    IF (OLD."ArtifactMd5" IS NOT NULL AND OLD."ArtifactMd5" IS DISTINCT FROM NEW."ArtifactMd5")
       OR (OLD."ArtifactSha1" IS NOT NULL AND OLD."ArtifactSha1" IS DISTINCT FROM NEW."ArtifactSha1") THEN
        RAISE EXCEPTION 'Measured artifact checksums are immutable' USING ERRCODE = '23514';
    END IF;
    IF (OLD."ArtifactMd5" IS NOT NULL OR OLD."ArtifactSha1" IS NOT NULL
        OR NEW."ArtifactMd5" IS NOT NULL OR NEW."ArtifactSha1" IS NOT NULL)
       AND (OLD."Sha256", OLD."Size") IS DISTINCT FROM (NEW."Sha256", NEW."Size") THEN
        RAISE EXCEPTION 'Measured artifact checksum identity is immutable' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END
$function$;

CREATE OR REPLACE FUNCTION romd.guard_rom_disc_reference()
 RETURNS trigger
 LANGUAGE plpgsql
 SET search_path TO ''
AS $function$
BEGIN
    RAISE EXCEPTION 'Retained disc references are immutable' USING ERRCODE = '23514';
END
$function$;
ALTER TABLE romd."Files" ADD CONSTRAINT "CK_Files_ArtifactMd5Length" CHECK ("ArtifactMd5" IS NULL OR octet_length("ArtifactMd5") = 16);
ALTER TABLE romd."Files" ADD CONSTRAINT "CK_Files_ArtifactSha1Length" CHECK ("ArtifactSha1" IS NULL OR octet_length("ArtifactSha1") = 20);
CREATE TRIGGER "TR_Files_ArtifactChecksums" BEFORE UPDATE ON romd."Files" FOR EACH ROW EXECUTE FUNCTION romd.guard_file_artifact_checksums();
CREATE TRIGGER rom_disc_reference_guard BEFORE UPDATE OR DELETE ON romd."RomDiscMediaReferences" FOR EACH ROW EXECUTE FUNCTION romd.guard_rom_disc_reference();

CREATE FUNCTION romd.guard_media_file_custody() RETURNS trigger LANGUAGE plpgsql SET search_path = '' AS $body$
BEGIN
    IF (NEW."Sha256",NEW."Size") IS DISTINCT FROM (OLD."Sha256",OLD."Size") AND (
        EXISTS (SELECT 1 FROM romd."MediaReconciliationPins" WHERE "FileId" = OLD."Id")) THEN
        RAISE EXCEPTION 'Active media sources retain immutable file identity' USING ERRCODE = '23514';
    END IF;
    IF (NEW."IsCompressed" OR NEW."SizeOnDisk" <> NEW."Size") AND EXISTS (
        SELECT 1 FROM romd."Representations" WHERE "FileId" = OLD."Id" AND "Encoding" = 'Chd') THEN
        RAISE EXCEPTION 'Native CHD artifacts retain raw physical storage' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END $body$;
CREATE TRIGGER "TR_Files_MediaCustody" BEFORE UPDATE ON romd."Files" FOR EACH ROW EXECUTE FUNCTION romd.guard_media_file_custody();
CREATE FUNCTION romd.guard_media_raw_representation() RETURNS trigger LANGUAGE plpgsql SET search_path = '' AS $body$
BEGIN
    IF NEW."Encoding" = 'Chd' AND NOT EXISTS (SELECT 1 FROM romd."Files" WHERE "Id" = NEW."FileId" AND NOT "IsCompressed" AND "SizeOnDisk" = "Size") THEN
        RAISE EXCEPTION 'Native CHD publication requires raw physical storage' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END $body$;
CREATE TRIGGER "TR_Representations_RawArtifact" BEFORE INSERT OR UPDATE ON romd."Representations"
    FOR EACH ROW EXECUTE FUNCTION romd.guard_media_raw_representation();

-- API catalogs may read bounded assertion metadata for retained logical identities, never raw source bodies.
CREATE FUNCTION romd.read_disc_reference_assertions(rom_ids integer[])
RETURNS TABLE ("RomId" integer,"Id" uuid,"System" text,"Media" text,"FileCount" integer,"LogicalBytes" bigint,
    "Sha1" bytea,"Md5" bytea,"Crc32" bigint,"RawReferenceBytes" bigint,"RawReferenceSha256" bytea,
    "ReferenceSource" text,"ParserProfile" text,"AcceptedSourceProvenance" text,"RetrievedAtUtcTicks" bigint)
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path = '' AS $body$
BEGIN
    IF rom_ids IS NULL OR cardinality(rom_ids) > 256 OR EXISTS (SELECT 1 FROM unnest(rom_ids) id WHERE id IS NULL OR id <= 0) THEN
        RAISE EXCEPTION 'Reference assertions require at most 256 positive logical identities' USING ERRCODE = '23514';
    END IF;
    RETURN QUERY SELECT logical."Id",r."Id",r."System"::text,r."Media"::text,r."FileCount",r."LogicalBytes",r."Sha1",r."Md5",r."Crc32",
        r."RawReferenceBytes",r."RawReferenceSha256",r."ReferenceSource"::text,r."ParserProfile"::text,r."AcceptedSourceProvenance"::text,r."RetrievedAtUtcTicks"
    FROM romd."Roms" logical
    CROSS JOIN LATERAL (SELECT * FROM romd."RomDiscMediaReferences" a
        WHERE a."LogicalBytes" = logical."ContentSize" AND a."Sha1" = logical."Sha1" AND a."Md5" = logical."Md5" AND
            a."Crc32" = get_byte(logical."Crc32",0)::bigint * 16777216 + get_byte(logical."Crc32",1)::bigint * 65536 +
                get_byte(logical."Crc32",2)::bigint * 256 + get_byte(logical."Crc32",3)::bigint
        -- Preserve the resolver's over-limit observation; truncation must never establish positive authority.
        ORDER BY a."Id" LIMIT 129) r
    WHERE logical."Id" = ANY (rom_ids) ORDER BY logical."Id",r."Id";
END $body$;
REVOKE ALL ON FUNCTION romd.read_disc_reference_assertions(integer[]) FROM PUBLIC;
