-- One durable family. Immutable input history carries no CAS ownership; only active pins do.
CREATE TABLE romd."MediaReconciliationJobs" (
    "JobId" uuid PRIMARY KEY REFERENCES romd."Jobs" ("Id") ON DELETE RESTRICT,
    "RequestMode" varchar(16) NOT NULL CHECK (("RequestMode" IN ('Subject','Selection')) IS TRUE),
    "SubjectKind" varchar(32), "SubjectId" integer,
    "OriginSelectionJobId" uuid REFERENCES romd."MediaReconciliationJobs" ("JobId") ON DELETE RESTRICT,
    "Filter" varchar(32), "KindId" varchar(64), "PlatformId" integer,
    "UpperRomId" integer, "UpperLayoutId" integer, "UpperRepresentationId" integer, "UpperCatalogFileId" integer, "Limit" bigint,
    "ConversionRequested" boolean, "CancelRequested" boolean NOT NULL,
    "CursorKind" varchar(32), "CursorId" integer,
    "SelectedCount" bigint, "CreatedCount" bigint, "AdoptedCount" bigint, "SkippedCount" bigint,
    "CompletedCount" bigint, "FailedCount" bigint, "EnumerationComplete" boolean,
    CHECK ((("RequestMode" = 'Subject' AND "SubjectKind" IS NOT NULL AND "SubjectKind" IN ('LogicalRom','OriginalLayout','Representation','CatalogCdLayout') AND "SubjectId" > 0 AND
            "Filter" IS NULL AND "KindId" IS NULL AND "PlatformId" IS NULL AND "UpperRomId" IS NULL AND "UpperLayoutId" IS NULL AND "UpperRepresentationId" IS NULL AND "UpperCatalogFileId" IS NULL AND
            "Limit" IS NULL AND "ConversionRequested" IS NULL AND "CursorKind" IS NULL AND "CursorId" IS NULL AND "SelectedCount" IS NULL AND
            "CreatedCount" IS NULL AND "AdoptedCount" IS NULL AND "SkippedCount" IS NULL AND "CompletedCount" IS NULL AND "FailedCount" IS NULL AND "EnumerationComplete" IS NULL) OR
        ("RequestMode" = 'Selection' AND "SubjectKind" IS NULL AND "SubjectId" IS NULL AND "OriginSelectionJobId" IS NULL AND
            "Filter" IS NOT NULL AND "Filter" IN ('AllSubjects','KindSubjects','RetainedRepresentations') AND
            (("Filter" = 'KindSubjects' AND "KindId" IS NOT NULL AND "KindId" ~ '^[a-z0-9-]{1,64}$' AND ("PlatformId" IS NULL OR "PlatformId" > 0)) OR
             ("Filter" <> 'KindSubjects' AND "KindId" IS NULL AND "PlatformId" IS NULL)) AND
            "UpperRomId" IS NOT NULL AND "UpperRomId" >= 0 AND "UpperLayoutId" IS NOT NULL AND "UpperLayoutId" >= 0 AND
            "UpperRepresentationId" IS NOT NULL AND "UpperRepresentationId" >= 0 AND "UpperCatalogFileId" IS NOT NULL AND "UpperCatalogFileId" >= 0 AND "Limit" IS NOT NULL AND "Limit" BETWEEN 1 AND 8589934588 AND
            "ConversionRequested" IS NOT NULL AND ("Filter" <> 'RetainedRepresentations' OR NOT "ConversionRequested") AND
            "SelectedCount" IS NOT NULL AND "SelectedCount" >= 0 AND "CreatedCount" IS NOT NULL AND "CreatedCount" >= 0 AND "AdoptedCount" IS NOT NULL AND "AdoptedCount" >= 0 AND
            "SkippedCount" IS NOT NULL AND "SkippedCount" >= 0 AND "CompletedCount" IS NOT NULL AND "CompletedCount" >= 0 AND "FailedCount" IS NOT NULL AND "FailedCount" >= 0 AND
            "EnumerationComplete" IS NOT NULL AND "CreatedCount" + "AdoptedCount" <= "Limit" AND "SelectedCount" = "CreatedCount" + "AdoptedCount" + "SkippedCount" AND
            "CompletedCount" <= "CreatedCount" + "AdoptedCount" AND "FailedCount" <= "CompletedCount" AND
            "SelectedCount" <= "UpperRomId"::bigint + "UpperLayoutId" + "UpperRepresentationId" + "UpperCatalogFileId" AND
            (("CursorKind" IS NULL AND "CursorId" IS NULL) OR ("CursorId" IS NOT NULL AND "CursorId" > 0 AND "CursorKind" IS NOT NULL AND
                (("CursorKind" = 'LogicalRom' AND "CursorId" <= "UpperRomId") OR ("CursorKind" = 'OriginalLayout' AND "CursorId" <= "UpperLayoutId") OR
                 ("CursorKind" = 'Representation' AND "CursorId" <= "UpperRepresentationId") OR ("CursorKind" = 'CatalogCdLayout' AND "CursorId" <= "UpperCatalogFileId")) AND ("Filter" <> 'RetainedRepresentations' OR "CursorKind" = 'Representation'))))) IS TRUE),
    CHECK (("OriginSelectionJobId" IS NULL OR "OriginSelectionJobId" <> "JobId") IS TRUE)
);
CREATE TABLE romd."MediaReconciliationSubjects" (
    "SubjectKind" varchar(32) NOT NULL CHECK (("SubjectKind" IN ('LogicalRom','OriginalLayout','Representation','CatalogCdLayout')) IS TRUE),
    "SubjectId" integer NOT NULL CHECK (("SubjectId" > 0) IS TRUE),
    "LatestJobId" uuid NOT NULL UNIQUE REFERENCES romd."MediaReconciliationJobs" ("JobId") ON DELETE RESTRICT,
    "RequestedVersion" bigint NOT NULL CHECK (("RequestedVersion" > 0) IS TRUE),
    "AcknowledgedVersion" bigint NOT NULL CHECK (("AcknowledgedVersion" BETWEEN 0 AND "RequestedVersion") IS TRUE),
    "ConversionRequested" boolean NOT NULL CHECK (("SubjectKind" <> 'Representation' OR NOT "ConversionRequested") IS TRUE),
    PRIMARY KEY ("SubjectKind","SubjectId")
);
CREATE TABLE romd."MediaReconciliationRequests" (
    "Id" uuid PRIMARY KEY CHECK (("Id" <> '00000000-0000-0000-0000-000000000000') IS TRUE),
    "JobId" uuid NOT NULL REFERENCES romd."MediaReconciliationJobs" ("JobId") ON DELETE RESTRICT,
    "OriginSelectionJobId" uuid REFERENCES romd."MediaReconciliationJobs" ("JobId") ON DELETE RESTRICT,
    "RetryOfJobId" uuid REFERENCES romd."MediaReconciliationJobs" ("JobId") ON DELETE RESTRICT,
    "ActorId" uuid CHECK (("ActorId" IS NULL OR "ActorId" <> '00000000-0000-0000-0000-000000000000') IS TRUE),
    "Cause" varchar(32) NOT NULL CHECK (("Cause" IN ('Admin','Import','Facts','Readiness','Selection','Retry')) IS TRUE),
    CHECK ((("Cause" = 'Retry') = ("RetryOfJobId" IS NOT NULL) AND ("RetryOfJobId" IS NULL OR "RetryOfJobId" <> "JobId")) IS TRUE),
    CHECK ((("Cause" = 'Selection') = ("OriginSelectionJobId" IS NOT NULL)) IS TRUE),
    CHECK (("Cause" NOT IN ('Admin','Retry') OR "ActorId" IS NOT NULL) IS TRUE),
    "RequestedVersion" bigint NOT NULL CHECK (("RequestedVersion" >= 0) IS TRUE), "ConversionRequested" boolean NOT NULL,
    "CreatedAtUtcTicks" bigint NOT NULL CHECK (("CreatedAtUtcTicks" BETWEEN 0 AND 3155378975999999999) IS TRUE)
);
CREATE INDEX "IX_MediaReconciliationRequests_Job" ON romd."MediaReconciliationRequests" ("JobId");
CREATE TABLE romd."MediaReconciliationAttempts" (
    "JobId" uuid NOT NULL REFERENCES romd."MediaReconciliationJobs" ("JobId") ON DELETE RESTRICT,
    "Number" integer NOT NULL CHECK (("Number" > 0) IS TRUE),
    "ExecutionFenceToken" uuid NOT NULL CHECK (("ExecutionFenceToken" <> '00000000-0000-0000-0000-000000000000') IS TRUE), "RequestedVersion" bigint NOT NULL CHECK (("RequestedVersion" > 0) IS TRUE),
    "Operation" varchar(16) NOT NULL CHECK (("Operation" IN ('Inspect','Verify','Encode','Release','Adopt')) IS TRUE),
    "KindId" varchar(64) CHECK (("KindId" IS NULL OR "KindId" ~ '^[a-z0-9-]{1,64}$') IS TRUE),
    "RecipeId" varchar(128) CHECK (("RecipeId" IS NULL OR (btrim("RecipeId") <> '' AND "RecipeId" !~ '[[:cntrl:]]')) IS TRUE),
    "SourceFingerprint" bytea NOT NULL CHECK ((octet_length("SourceFingerprint") = 32 AND "SourceFingerprint" <> decode(repeat('00',32),'hex')) IS TRUE),
    "CapabilityFingerprint" bytea NOT NULL CHECK ((octet_length("CapabilityFingerprint") = 32 AND "CapabilityFingerprint" <> decode(repeat('00',32),'hex')) IS TRUE),
    "LimitsFingerprint" bytea NOT NULL CHECK ((octet_length("LimitsFingerprint") = 32 AND "LimitsFingerprint" <> decode(repeat('00',32),'hex')) IS TRUE),
    "Fingerprint" bytea NOT NULL CHECK ((octet_length("Fingerprint") = 32 AND "Fingerprint" <> decode(repeat('00',32),'hex')) IS TRUE),
    "SourceCount" smallint NOT NULL CHECK (("SourceCount" BETWEEN 1 AND 256) IS TRUE),
    "DecoderBuild" varchar(256) NOT NULL CHECK ((btrim("DecoderBuild") <> '' AND "DecoderBuild" !~ '[[:cntrl:]]') IS TRUE),
    "DecoderRid" varchar(32), "DecoderUpstreamCommit" bytea, "DecoderRecipeVersion" integer,
    "EncoderBuild" varchar(256), "DiscKind" varchar(32),
    "OriginalLayoutId" integer, "CatalogFileId" integer, "CatalogBindingId" integer, "LayoutRevision" uuid, "PlatformId" integer, "ReleaseId" integer,
    "CatalogFingerprint" varchar(128), "EntryPoint" varchar(240),
    "LogicalInspectionIds" uuid[] NOT NULL CHECK ((cardinality("LogicalInspectionIds") <= 256 AND array_position("LogicalInspectionIds",NULL) IS NULL AND
        array_position("LogicalInspectionIds",'00000000-0000-0000-0000-000000000000'::uuid) IS NULL) IS TRUE),
    "ReferenceFingerprint" bytea CHECK (("ReferenceFingerprint" IS NULL OR (octet_length("ReferenceFingerprint") = 32 AND "ReferenceFingerprint" <> decode(repeat('00',32),'hex'))) IS TRUE),
    "StartedAtUtcTicks" bigint NOT NULL CHECK (("StartedAtUtcTicks" BETWEEN 0 AND 3155378975999999999) IS TRUE),
    "Outcome" varchar(32), "OutcomeCode" varchar(128), "Deterministic" boolean,
    "ResultRepresentationId" integer, "ResultVerificationId" uuid, "ResultCreationId" uuid, "ResultInspectionId" uuid,
    "CompletedAtUtcTicks" bigint, "StoppedAtUtcTicks" bigint,
    PRIMARY KEY ("JobId","Number"),
    CHECK ((("DecoderRid" IS NULL AND "DecoderUpstreamCommit" IS NULL AND "DecoderRecipeVersion" IS NULL) OR
        ("DecoderRid" IS NOT NULL AND "DecoderRid" ~ '^[a-z0-9.-]{1,32}$' AND "DecoderUpstreamCommit" IS NOT NULL AND
         octet_length("DecoderUpstreamCommit") = 20 AND "DecoderUpstreamCommit" <> decode(repeat('00',20),'hex') AND "DecoderRecipeVersion" IS NOT NULL AND "DecoderRecipeVersion" > 0)) IS TRUE),
    CHECK (("Operation" <> 'Encode' OR ("RecipeId" IS NOT NULL AND "KindId" IS NOT NULL AND "EncoderBuild" IS NOT NULL)) IS TRUE),
    CHECK (("Operation" <> 'Adopt' OR ("RecipeId" IS NOT NULL AND "KindId" IS NOT NULL AND "EncoderBuild" IS NULL AND
        "DiscKind" IS NULL AND "DecoderRid" IS NOT NULL AND "OriginalLayoutId" IS NOT NULL)) IS TRUE),
    CHECK (("EncoderBuild" IS NULL OR (btrim("EncoderBuild") <> '' AND "EncoderBuild" !~ '[[:cntrl:]]')) IS TRUE),
    CHECK (("DiscKind" IS NULL OR "DiscKind" IN ('GameCube','Wii','PlayStation2Dvd')) IS TRUE),
    CHECK ((("OriginalLayoutId" IS NULL AND "CatalogFileId" IS NULL AND "CatalogBindingId" IS NULL AND "LayoutRevision" IS NULL AND
            "PlatformId" IS NULL AND "ReleaseId" IS NULL AND "CatalogFingerprint" IS NULL AND "EntryPoint" IS NULL) OR
        ((("OriginalLayoutId" > 0 AND "CatalogFileId" IS NULL AND "CatalogBindingId" IS NULL) OR
          ("OriginalLayoutId" IS NULL AND "CatalogFileId" > 0 AND "CatalogBindingId" > 0)) AND
         "LayoutRevision" IS NOT NULL AND "LayoutRevision" <> '00000000-0000-0000-0000-000000000000' AND "PlatformId" > 0 AND "ReleaseId" > 0 AND
         "CatalogFingerprint" IS NOT NULL AND btrim("CatalogFingerprint") <> '' AND "CatalogFingerprint" !~ '[[:cntrl:]]' AND
         "EntryPoint" IS NOT NULL AND btrim("EntryPoint") <> '' AND "EntryPoint" !~ '[[:cntrl:]]')) IS TRUE),
    CHECK ((("Outcome" IS NULL AND "OutcomeCode" IS NULL AND "Deterministic" IS NULL AND "CompletedAtUtcTicks" IS NULL AND "StoppedAtUtcTicks" IS NULL AND
        "ResultRepresentationId" IS NULL AND "ResultVerificationId" IS NULL AND "ResultCreationId" IS NULL AND "ResultInspectionId" IS NULL) OR
        ("Outcome" IS NOT NULL AND "Outcome" IN ('Recorded','Failed','Superseded','Cancelled') AND "OutcomeCode" IS NOT NULL AND btrim("OutcomeCode") <> '' AND "OutcomeCode" !~ '[[:cntrl:]]' AND
         "Deterministic" IS NOT NULL AND (NOT "Deterministic" OR "Outcome" = 'Failed') AND "CompletedAtUtcTicks" IS NOT NULL AND
         "CompletedAtUtcTicks" BETWEEN "StartedAtUtcTicks" AND 3155378975999999999 AND "StoppedAtUtcTicks" IS NOT NULL AND "StoppedAtUtcTicks" BETWEEN "StartedAtUtcTicks" AND "CompletedAtUtcTicks" AND
         (("Outcome" = 'Recorded' AND "ResultRepresentationId" > 0 AND "ResultVerificationId" IS NOT NULL AND "ResultVerificationId" <> '00000000-0000-0000-0000-000000000000' AND
             (("Operation" = 'Encode' AND "ResultCreationId" IS NOT NULL AND "ResultCreationId" <> '00000000-0000-0000-0000-000000000000') OR ("Operation" <> 'Encode' AND "ResultCreationId" IS NULL)) AND
             (("Operation" = 'Inspect' AND "ResultInspectionId" IS NOT NULL AND "ResultInspectionId" <> '00000000-0000-0000-0000-000000000000') OR ("Operation" <> 'Inspect' AND "ResultInspectionId" IS NULL))) OR
          ("Outcome" <> 'Recorded' AND "ResultRepresentationId" IS NULL AND "ResultVerificationId" IS NULL AND "ResultCreationId" IS NULL AND "ResultInspectionId" IS NULL)))) IS TRUE)
);
CREATE UNIQUE INDEX "IX_MediaReconciliationAttempts_Active" ON romd."MediaReconciliationAttempts" ("JobId") WHERE "Outcome" IS NULL;
-- Historical facts intentionally have no FK to live media rows: retention is expressed by the separate pins below.
CREATE TABLE romd."MediaReconciliationInputs" (
    "JobId" uuid NOT NULL, "AttemptNumber" integer NOT NULL, "Ordinal" smallint NOT NULL CHECK (("Ordinal" BETWEEN 0 AND 255) IS TRUE),
    "Role" varchar(16) NOT NULL CHECK (("Role" IN ('Whole','Bin','Cue','Sidecar','Artifact')) IS TRUE), "Path" varchar(500),
    "FileId" integer NOT NULL CHECK (("FileId" > 0) IS TRUE), "ArtifactSha256" bytea NOT NULL CHECK ((octet_length("ArtifactSha256") = 32 AND "ArtifactSha256" <> decode(repeat('00',32),'hex')) IS TRUE),
    "ArtifactSize" bigint NOT NULL CHECK (("ArtifactSize" >= 0) IS TRUE), "RepresentationId" integer, "OutputId" integer, "RomId" integer, "VerificationId" uuid,
    "ParametersSha256" bytea CHECK (("ParametersSha256" IS NULL OR (octet_length("ParametersSha256") = 32 AND "ParametersSha256" <> decode(repeat('00',32),'hex'))) IS TRUE),
    "ContentSha256" bytea, "ContentSize" bigint, "Sha1" bytea, "Md5" bytea, "Crc32" bytea,
    "WitnessCatalogFileId" integer, "WitnessBindingId" integer, "WitnessRevision" uuid,
    PRIMARY KEY ("JobId","AttemptNumber","Ordinal"),
    FOREIGN KEY ("JobId","AttemptNumber") REFERENCES romd."MediaReconciliationAttempts" ("JobId","Number") ON DELETE RESTRICT,
    CHECK ((("Role" = 'Artifact' AND "Path" IS NULL AND "RepresentationId" > 0 AND "OutputId" IS NULL AND "RomId" IS NULL AND
        "ContentSha256" IS NULL AND "ContentSize" IS NULL AND "Sha1" IS NULL AND "Md5" IS NULL AND "Crc32" IS NULL) OR
        ("Role" <> 'Artifact' AND "Path" IS NOT NULL AND btrim("Path") <> '' AND "Path" !~ '[[:cntrl:]]' AND
         "ContentSha256" IS NOT NULL AND octet_length("ContentSha256") = 32 AND "ContentSha256" <> decode(repeat('00',32),'hex') AND "ContentSize" IS NOT NULL AND "ContentSize" >= 0 AND
         "Sha1" IS NOT NULL AND octet_length("Sha1") = 20 AND "Sha1" <> decode(repeat('00',20),'hex') AND "Md5" IS NOT NULL AND octet_length("Md5") = 16 AND "Md5" <> decode(repeat('00',16),'hex') AND "Crc32" IS NOT NULL AND octet_length("Crc32") = 4 AND
         (("Role" IN ('Whole','Bin') AND "RepresentationId" > 0 AND "OutputId" > 0 AND "RomId" > 0 AND "VerificationId" IS NOT NULL) OR
          ("Role" IN ('Cue','Sidecar') AND "RepresentationId" IS NULL AND "OutputId" IS NULL AND "RomId" IS NULL AND "VerificationId" IS NULL AND "ParametersSha256" IS NULL AND
            "ArtifactSha256" = "ContentSha256" AND "ArtifactSize" = "ContentSize")))) IS TRUE),
    CHECK (("VerificationId" IS NULL OR "VerificationId" <> '00000000-0000-0000-0000-000000000000') IS TRUE),
    CHECK ((("WitnessCatalogFileId" IS NULL AND "WitnessBindingId" IS NULL AND "WitnessRevision" IS NULL) OR
        ("Role" = 'Artifact' AND "WitnessCatalogFileId" > 0 AND "WitnessBindingId" > 0 AND "WitnessRevision" IS NOT NULL AND
            "WitnessRevision" <> '00000000-0000-0000-0000-000000000000')) IS TRUE)
);
CREATE UNIQUE INDEX "IX_MediaReconciliationInputs_CatalogWitness" ON romd."MediaReconciliationInputs" ("JobId","AttemptNumber") WHERE "WitnessBindingId" IS NOT NULL;
CREATE UNIQUE INDEX "IX_MediaReconciliationInputs_Path" ON romd."MediaReconciliationInputs" ("JobId","AttemptNumber",lower("Path")) WHERE "Path" IS NOT NULL;
CREATE TABLE romd."MediaReconciliationPins" (
    "JobId" uuid NOT NULL, "AttemptNumber" integer NOT NULL, "Ordinal" smallint NOT NULL,
    "FileId" integer NOT NULL REFERENCES romd."Files" ("Id") ON DELETE RESTRICT,
    "RepresentationId" integer REFERENCES romd."Representations" ("Id") ON DELETE RESTRICT,
    "OutputId" integer REFERENCES romd."RepresentationOutputs" ("Id") ON DELETE RESTRICT,
    "VerificationId" uuid REFERENCES romd."Verifications" ("Id") ON DELETE RESTRICT,
    PRIMARY KEY ("JobId","AttemptNumber","Ordinal"),
    FOREIGN KEY ("JobId","AttemptNumber","Ordinal") REFERENCES romd."MediaReconciliationInputs" ("JobId","AttemptNumber","Ordinal") ON DELETE RESTRICT
);
CREATE INDEX "IX_MediaReconciliationPins_Representation" ON romd."MediaReconciliationPins" ("RepresentationId");
CREATE INDEX "IX_MediaReconciliationPins_File" ON romd."MediaReconciliationPins" ("FileId");
CREATE TABLE romd."MediaReconciliationFailures" (
    "Fingerprint" bytea PRIMARY KEY CHECK ((octet_length("Fingerprint") = 32 AND "Fingerprint" <> decode(repeat('00',32),'hex')) IS TRUE),
    "JobId" uuid NOT NULL, "AttemptNumber" integer NOT NULL,
    FOREIGN KEY ("JobId","AttemptNumber") REFERENCES romd."MediaReconciliationAttempts" ("JobId","Number") ON DELETE RESTRICT
);
CREATE TABLE romd."MediaReconciliationOutcomes" (
    "JobId" uuid PRIMARY KEY REFERENCES romd."MediaReconciliationJobs" ("JobId") ON DELETE RESTRICT,
    "Kind" varchar(32) NOT NULL CHECK (("Kind" IN ('Completed','CompletedWithErrors','Superseded','Failed','Cancelled')) IS TRUE),
    "Code" varchar(128) NOT NULL CHECK ((btrim("Code") <> '' AND "Code" !~ '[[:cntrl:]]') IS TRUE),
    "AcknowledgedVersion" bigint NOT NULL CHECK (("AcknowledgedVersion" >= 0) IS TRUE),
    "ExecutionFenceToken" uuid NOT NULL CHECK (("ExecutionFenceToken" <> '00000000-0000-0000-0000-000000000000') IS TRUE),
    "CompletedAtUtcTicks" bigint NOT NULL CHECK (("CompletedAtUtcTicks" BETWEEN 0 AND 3155378975999999999) IS TRUE)
);
CREATE TABLE romd."MediaReconciliationSelectionItems" (
    "SelectionJobId" uuid NOT NULL REFERENCES romd."MediaReconciliationJobs" ("JobId") ON DELETE RESTRICT,
    "SubjectKind" varchar(32) NOT NULL CHECK (("SubjectKind" IN ('LogicalRom','OriginalLayout','Representation','CatalogCdLayout')) IS TRUE),
    "SubjectId" integer NOT NULL CHECK (("SubjectId" > 0) IS TRUE), "Ordinal" bigint NOT NULL CHECK (("Ordinal" > 0) IS TRUE),
    "ChildJobId" uuid REFERENCES romd."MediaReconciliationJobs" ("JobId") ON DELETE RESTRICT,
    "IsOwned" boolean NOT NULL, "SkippedCode" varchar(128),
    PRIMARY KEY ("SelectionJobId","SubjectKind","SubjectId"), UNIQUE ("SelectionJobId","Ordinal"),
    CHECK ((("ChildJobId" IS NOT NULL AND "SkippedCode" IS NULL) OR
        ("ChildJobId" IS NULL AND NOT "IsOwned" AND "SkippedCode" IS NOT NULL AND btrim("SkippedCode") <> '' AND "SkippedCode" !~ '[[:cntrl:]]')) IS TRUE)
);
