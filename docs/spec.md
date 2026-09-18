# ROMD: Design Specification & Strategic Roadmap
*Management Service for Digital Game Preservation*

**Target Scale:** 1M+ ROM files  
**Scope:** Single-file ROMs and disc images only (no folder-based games... YET)

---

## Executive Summary

ROMD is a **Management Service for Digital Game Preservation** that also serves external game clients. This specification defines the Admin UI as a **Curator's Workbench** focused on three core workflows:

1. **Triage** (Inbox) - Processing unmatched files
2. **Verification** (Integrity) - Ensuring hash conformance through associations
3. **Curation** (Enrichment) - Building metadata and collections

**Key Architectural Principles:**
- **Immutability First:** DAT Definitions and File storage are immutable; only associations change
- **Content-Addressable Storage:** Unified CAS layer (Romd.Storage) for all file types
- **Verification as Relationship:** Conformance state lives on associations, not entities
- **Application-Layer Atomicity:** Reference counting managed via transaction-wrapped atomic SQL
- **Server Neutrality:** No "primary" or "recommended" versions - clients choose
- **Multi-DAT Support:** Files can match multiple Definitions simultaneously
- **Scale-First:** Server-side pagination, FTS, and materialized views for 1M+ operations

**Explicit Scope Exclusions (v2.4):**
- ❌ Folder-based games (PS3 PKG, WiiU decrypted dumps)
- ❌ Multi-tier storage (hot/cold separation)
- ❌ Cloud storage backends (S3/Azure) - local FileSystem only
- ✅ Single-file ROMs (NES, SNES, Genesis)
- ✅ Disc images (PSX ISO, Dreamcast CDI)
- ✅ Multi-disc games (stored as separate FileEntities)

---

## 1. Product Vision & Core Philosophy

### The "Server-First" Constraint
Players interact with ROMD via external clients (emulators, frontends). The Admin UI is **not for playing**; it is for **manufacturing the library**.

- **Input:** Chaos (Archives, cryptic filenames, updated Definitions)
- **Process:** Identification, Verification, Enrichment, Organization
- **Output:** Order (Clean API, curated collections, verified integrity)

### The "Trust" Philosophy

**1. Immutability Guarantee**
- DAT Definitions are canonical external data - never modified, only replaced
- Files are content-addressed by hash - identity is immutable
- Only associations and metadata can change

**2. The Preservation Axiom**  
*"The definition of truth changes, but the artifact does not."*

When No-Intro updates a DAT with corrected hashes, the ROM files on disk remain unchanged. The system creates new associations while preserving the historical record.

**3. Verification as Relationship**  
Verification is not a file property - it's a relationship property:
- File + Definition → Association → Conformance State
- Same file can be Verified against one DAT and Suspect against another

**4. Neutrality**  
The server presents all available Versions without preference. The client or end-user decides which to use (USA vs Japan, v1.0 vs v1.1).

---

## 2. Core Data Architecture

### 2.1 The Trinity Pattern: Asset / Definition / Association

```
┌─────────────────────────────────────────────────┐
│ BLOB LAYER (Romd.Storage)                       │
│ IContentAddressableStore - Pure CAS operations  │
│ ├─ Content identified by SHA-256 hash           │
│ ├─ Transparently compressed (zstd)              │
│ ├─ Smart compression skipping for pre-compressed│
│ ├─ Integrity verification on retrieval          │
│ └─ No database knowledge - just blobs           │
└─────────────────────────────────────────────────┘
↑
┌─────────────────────────────────────────────────┐
│ APPLICATION LAYER (Romd.Storage.Application)    │
│ IStorageService - FileEntity lifecycle          │
│ ├─ Reference counting (app-layer atomic)        │
│ ├─ Coordinates blob storage with database       │
│ └─ Wraps IContentAddressableStore               │
└─────────────────────────────────────────────────┘
↑
┌───────────┼───────────┬──────────┐
│           │           │          │
┌───────┴────┐ ┌────┴─────┐ ┌──┴────────┐ ┌┴─────────┐
│ RomFile    │ │ BiosFile │ │ MediaFile │ │ DatFile  │
│ (Library)  │ │ (System) │ │ (Catalog) │ │(Registry)│
└────────────┘ └──────────┘ └───────────┘ └──────────┘

┌─────────────────────────────────────────────────┐
│ CANONICAL LAYER (Immutable)                     │
│ DatFile, DatGame, DatRom                        │
│ └─> External authority (No-Intro, Redump, etc.) │
│     APPEND-ONLY: Never UPDATE'd after import    │
└─────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────┐
│ ASSOCIATION LAYER (Mutable Relationships)       │
│ DatRomAssociations - Bridges requirements       │
│ ├─ DatRom (what's expected)                     │
│ ├─ FileEntity (what we have)                    │
│ └─ ConformanceState (verification result)       │
│                                                 │
│ UserFileAssociations - Manual curator links     │
│ └─> Separate from canonical (preserves purity)  │
└─────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────┐
│ READ MODELS (CQRS - Performance Cache)          │
│ SystemStats, PlatformStats                      │
│ └─> Materialized views updated by jobs          │
│     Dashboard queries these, not raw tables     │
└─────────────────────────────────────────────────┘
```

---

### 2.2 Content-Addressable Storage (Romd.Storage)

**Critical Design Decision: The Carmack Principle**  
*"Ingest is work. Storage is simple."*

**Hash Strategy:**

All four hashes are computed in parallel during ingest:

| Hash | Length | Purpose | Storage | DAT Matching |
|------|--------|---------|---------|--------------|
| SHA-256 | 32 bytes | Content identity, storage key derivation | Required | Rare |
| SHA-1 | 20 bytes | DAT matching (No-Intro, Redump, TOSEC) | Required | Primary |
| MD5 | 16 bytes | Legacy DAT compatibility | Optional | Secondary |
| CRC32 | 4 bytes | Legacy DAT compatibility, quick checks | Optional | Tertiary |

**Why SHA-256 for Storage Identity:**
- Stronger collision resistance (256-bit vs 160-bit)
- Modern standard for content-addressable storage
- SHA-1 collision attacks are practical (SHAttered, 2017)
- Future-proof against cryptographic advances

**Why SHA-1 for DAT Matching:**
- Industry standard: No-Intro, Redump, TOSEC all use SHA-1
- Backwards compatibility with existing DAT ecosystems
- Collision attacks require crafted files, not random ROMs

**Compression Strategy (FINALIZED):**
```
User uploads: roms.zip (500 files)
↓
System extracts: 500 individual ROM files (streaming, not full extraction to disk)
↓
Each file hashed: SHA-256 + SHA-1 + MD5 + CRC32 (parallel computation)
↓
Each file compressed: zstd level 3 (if beneficial)
↓
Compression ratio checked: If ratio > 0.95 (less than 5% savings), store uncompressed
↓
Each file stored: /storage/ab/cd/abcd1234....zst (or .raw if uncompressed)
↓
Archive metadata: Stored on RomFile (SourceArchive="roms.zip", PathInArchive="snes/game.sfc")
```

**Smart Compression:**
- Pre-compressed files (ZIP, JPEG, PNG, MP3) don't benefit from re-compression
- `MinCompressionRatio` option (default 0.95) determines threshold
- Files that don't compress well are stored with `.raw` extension
- Avoids wasting CPU cycles and potential size increase

**Why NOT store the zip as FileEntity?**
- Cannot verify individual files without extraction on every query
- Different users upload different zips containing same ROM
- Defeats CAS deduplication (50 zips vs 1 file = 50x storage waste)

**Physical Storage Structure:**
```
/storage/
├─ ab/
│  └─ cd/
│     ├─ abcd1234...5678.zst    (compressed ROM)
│     └─ abcd9876...4321.raw    (uncompressed - already compressed format)
├─ ef/
│  └─ 01/
│     └─ ef012345...6789.zst
...

Sharding: First 4 hex characters (2 bytes) of SHA-256 → 256 × 256 = 65,536 max directories
Avoids filesystem limits on files-per-directory
```

---

### 2.3 Reference Counting (Application-Layer Atomic Updates)

**FINALIZED APPROACH: No SQL Triggers**

Reference counting is managed by `IStorageService` in the application layer, which wraps `IContentAddressableStore` and coordinates with the database.

**Domain Service (Romd.Storage.Application):**

```csharp
namespace Romd.Storage.Application;

/// <summary>
/// Manages FileEntity lifecycle including reference counting.
/// All RefCount mutations go through this service to ensure atomicity.
/// </summary>
public interface IStorageService
{
    /// <summary>
    /// Stores content and creates/updates FileEntity.
    /// Handles deduplication automatically via underlying CAS.
    /// </summary>
    Task<StoreFileResult> StoreFileAsync(
        Stream content, 
        string? filename = null,
        IProgress<StoreProgress>? progress = null,
        CancellationToken ct = default);
    
    /// <summary>
    /// Retrieves file content with integrity verification.
    /// </summary>
    Task<Stream> RetrieveFileAsync(int fileEntityId, CancellationToken ct = default);
    
    /// <summary>
    /// Atomically increments reference count.
    /// Called when a domain entity (RomFile, BiosFile, etc.) is created.
    /// </summary>
    Task ClaimReferenceAsync(int fileEntityId, CancellationToken ct = default);
    
    /// <summary>
    /// Atomically decrements reference count.
    /// Called when a domain entity is deleted.
    /// </summary>
    Task ReleaseReferenceAsync(int fileEntityId, CancellationToken ct = default);
    
    /// <summary>
    /// Garbage collects orphaned files (RefCount=0, past grace period).
    /// </summary>
    Task<GarbageCollectResult> GarbageCollectAsync(
        TimeSpan gracePeriod,
        CancellationToken ct = default);
}

public sealed class StorageService : IStorageService
{
    private readonly IContentAddressableStore _cas;
    private readonly AppDbContext _context;
    
    public async Task ClaimReferenceAsync(int fileEntityId, CancellationToken ct)
    {
        // Generates: UPDATE FileEntities SET RefCount = RefCount + 1 WHERE Id = ?
        var rowsAffected = await _context.FileEntities
            .Where(f => f.Id == fileEntityId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(f => f.RefCount, f => f.RefCount + 1), 
                ct);
                
        if (rowsAffected == 0)
            throw new InvalidOperationException($"FileEntity {fileEntityId} not found");
    }
    
    public async Task ReleaseReferenceAsync(int fileEntityId, CancellationToken ct)
    {
        // Atomic decrement
        var rowsAffected = await _context.FileEntities
            .Where(f => f.Id == fileEntityId)
            .Where(f => f.RefCount > 0)  // Safety: prevent negative
            .ExecuteUpdateAsync(
                s => s.SetProperty(f => f.RefCount, f => f.RefCount - 1), 
                ct);
                
        if (rowsAffected == 0)
            throw new InvalidOperationException($"FileEntity {fileEntityId} not found or RefCount already 0");
    }
    
    public async Task<StoreFileResult> StoreFileAsync(
        Stream content,
        string? filename = null,
        IProgress<StoreProgress>? progress = null,
        CancellationToken ct = default)
    {
        // 1. Store blob via CAS (handles dedup internally)
        var result = await _cas.StoreAsync(content, progress, ct);
        
        // 2. Find or create FileEntity
        var sha256Bytes = result.Hash.Sha256.ToArray();
        var entity = await _context.FileEntities
            .FirstOrDefaultAsync(f => f.Sha256 == sha256Bytes, ct);
        
        bool wasDeduplicated = entity != null;
        
        if (entity == null)
        {
            entity = new FileEntity {
                Sha256 = sha256Bytes,
                Sha1 = result.Hash.Sha1.ToArray(),
                Md5 = result.Hash.Md5.ToArray(),
                Crc32 = result.Hash.Crc32.ToArray(),
                Size = result.Size,
                CompressedSize = result.CompressedSize,
                IsCompressed = result.IsCompressed,
                RefCount = 0,
                CreatedAt = DateTime.UtcNow
            };
            
            _context.FileEntities.Add(entity);
            await _context.SaveChangesAsync(ct);
        }
        
        return new StoreFileResult(
            entity.Id,
            result.Hash,
            result.Key,
            result.Size,
            result.CompressedSize,
            wasDeduplicated,
            result.IsCompressed);
    }
}
```

**Usage in Application Layer:**

```csharp
// Application/Features/Roms/DeleteRomCommandHandler.cs

public async Task<Unit> Handle(DeleteRomCommand request, CancellationToken ct)
{
    await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
    
    try
    {
        // 1. Get the ROM
        var rom = await _romRepo.GetByIdAsync(request.RomId, ct);
        
        // 2. Delete domain entity
        await _romRepo.DeleteAsync(rom, ct);
        
        // 3. Atomically decrement FileEntity.RefCount
        await _storageService.ReleaseReferenceAsync(rom.FileEntityId, ct);
        
        await transaction.CommitAsync(ct);
        
        return Unit.Value;
    }
    catch
    {
        await transaction.RollbackAsync(ct);
        throw;
    }
}
```

**Why This is Better Than Triggers:**
- ✅ **Explicit:** Code clearly shows RefCount management
- ✅ **Testable:** Unit tests can mock IStorageService
- ✅ **Atomic:** Wrapped in transaction (rollback on failure)
- ✅ **Flexible:** Same service works for RomFile, BiosFile, MediaFile, DatFile
- ✅ **No Hidden Logic:** Developers see exactly what happens

---

### 2.4 SQLite Concurrency Strategy

**Problem:** SQLite allows only 1 writer at a time (even in WAL mode).

**Solution: Serialize Writes, Parallelize Reads/Hashing**

**Ingest Pipeline Architecture:**

```
┌─────────────────────────────────────────────────┐
│ UPLOAD QUEUE (Single-threaded write serializer) │
│                                                 │
│  Thread 1: Write to DB                          │
│  ├─ Create RomFile                              │
│  ├─ Create DatRomAssociations                   │
│  └─ Update SystemStats                          │
│                                                 │
│  Throughput: ~100-200 DB writes/sec             │
└─────────────────────────────────────────────────┘
                    ↑
                    │ (Queues work items)
                    │
┌─────────────────────────────────────────────────┐
│ HASH WORKERS (8 parallel threads)               │
│                                                 │
│  Worker 1-8: Compute hashes (CPU-bound)         │
│  ├─ Read file stream                            │
│  ├─ SHA-256 + SHA-1 + MD5 + CRC32 computation   │
│  ├─ Find matching DatRoms (read-only query)     │
│  └─ Queue result to write serializer            │
│                                                 │
│  Throughput: ~50-80 files/sec (I/O limited)     │
└─────────────────────────────────────────────────┘
```

**Implementation (Hangfire Background Job):**

```csharp
public class IngestRomJob
{
    private readonly IStorageService _storage;
    private readonly IMediator _mediator;
    private readonly SemaphoreSlim _writeLock = new(1, 1); // Single writer
    
    public async Task ProcessArchiveAsync(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        
        // Phase 1: Hash in parallel (read-only, no DB writes)
        var hashTasks = archive.Entries
            .Where(e => !e.FullName.EndsWith('/'))  // Skip directories
            .Select(async entry => {
                await using var stream = entry.Open();
                var hash = await ContentHash.ComputeAsync(stream);
                return new { Entry = entry, Hash = hash };
            });
            
        var hashedFiles = await Task.WhenAll(hashTasks);
        
        // Phase 2: Write to DB serially (avoids SQLite locking)
        foreach (var file in hashedFiles)
        {
            await _writeLock.WaitAsync();
            try
            {
                await using var transaction = await _unitOfWork.BeginTransactionAsync();
                
                // Store via IStorageService (dedupe + compress + FileEntity)
                var storeResult = await _storage.StoreFileAsync(
                    file.Entry.Open(), 
                    file.Entry.FullName);
                
                // Create RomFile
                var cmd = new CreateRomFileCommand(
                    storeResult.FileEntityId,
                    file.Entry.Name,
                    archivePath,
                    file.Entry.FullName);
                    
                await _mediator.Send(cmd);
                
                await transaction.CommitAsync();
            }
            finally
            {
                _writeLock.Release();
            }
        }
    }
}
```

**Configuration:**
```json
{
  "Storage": {
    "MaxConcurrentHashWorkers": 8,
    "WriteQueueBatchSize": 100,
    "CommandTimeout": "00:05:00"
  }
}
```

---

## 3. Data Model Specification (FINALIZED)

### 3.1 Romd.Storage (Blob Layer)

The blob layer (`IContentAddressableStore`) is a pure content-addressable store with no database knowledge. It handles:
- Content storage and retrieval by SHA-256 hash
- Transparent compression/decompression
- Integrity verification on retrieval
- Deduplication coordination for concurrent stores

**No database schema** - this layer only deals with files on disk.

---

### 3.2 FileEntity (Application Layer)

The application layer tracks FileEntities in the database, linking blob storage to domain entities.

**Schema:**
```sql
CREATE TABLE FileEntities (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,

    -- Content address (immutable)
    Sha256 BLOB(32) NOT NULL UNIQUE,        -- Primary content identifier, used for storage retrieval
    Sha1 BLOB(20) NOT NULL,                 -- DAT matching (most DATs use this)
    Md5 BLOB(16),                           -- Legacy DAT compatibility
    Crc32 BLOB(4),                          -- Legacy DAT compatibility

    -- Storage metadata
    Size INTEGER NOT NULL,                  -- Uncompressed size
    CompressedSize INTEGER,                 -- Actual disk usage
    IsCompressed INTEGER NOT NULL,          -- 1 if .zst, 0 if .raw
    MimeType TEXT,

    -- Reference counting (MANAGED BY APPLICATION LAYER)
    RefCount INTEGER NOT NULL DEFAULT 0,

    -- Metadata
    CreatedAt TEXT NOT NULL,
    SourceUrl TEXT
);

CREATE UNIQUE INDEX idx_file_sha256 ON FileEntities(Sha256);
CREATE INDEX idx_file_sha1 ON FileEntities(Sha1);
CREATE INDEX idx_file_md5 ON FileEntities(Md5) WHERE Md5 IS NOT NULL;
CREATE INDEX idx_file_crc32 ON FileEntities(Crc32) WHERE Crc32 IS NOT NULL;
CREATE INDEX idx_file_orphans ON FileEntities(RefCount, CreatedAt) 
  WHERE RefCount = 0;
```

**NO SQL TRIGGERS.** RefCount managed by `IStorageService` in application layer.

**Garbage Collection:**
```csharp
// Hangfire daily job
public async Task GarbageCollectOrphansAsync()
{
    var gracePeriod = TimeSpan.FromDays(30);
    var cutoffDate = DateTime.UtcNow - gracePeriod;
    
    var orphans = await _context.FileEntities
        .Where(f => f.RefCount == 0)
        .Where(f => f.CreatedAt < cutoffDate)
        .ToListAsync();
    
    foreach (var orphan in orphans)
    {
        // Parse storage key from path
        if (StorageKey.TryParse(orphan.StoragePath, out var key))
        {
            // Delete physical file via CAS
            await _cas.DeleteAsync(key);
        }
        
        // Delete entity
        _context.FileEntities.Remove(orphan);
    }
    
    await _context.SaveChangesAsync();
    
    _logger.LogInformation(
        "GC completed: {Count} orphans deleted, {Bytes} reclaimed",
        orphans.Count,
        orphans.Sum(o => o.CompressedSize ?? o.Size));
}
```

---

### 3.3 Domain Projections

**RomFile:**
```sql
CREATE TABLE RomFiles (
  Id INTEGER PRIMARY KEY AUTOINCREMENT,
  FileEntityId INTEGER NOT NULL UNIQUE REFERENCES FileEntities(Id) ON DELETE CASCADE,
  
  -- Domain metadata (immutable)
  OriginalFilename TEXT NOT NULL,
  ImportedAt TEXT NOT NULL,
  ImportedBy TEXT,
  
  -- Archive context
  SourceArchive TEXT,           -- "roms.zip" (if from archive)
  PathInArchive TEXT            -- "snes/super_mario.sfc"
);

CREATE INDEX idx_romfile_imported ON RomFiles(ImportedAt DESC);
CREATE INDEX idx_romfile_filename ON RomFiles(OriginalFilename COLLATE NOCASE);
```

**Application Layer (Create):**
```csharp
public async Task<int> CreateRomFileAsync(
    int fileEntityId,
    string originalFilename,
    Guid? importedBy = null,
    string? sourceArchive = null,
    string? pathInArchive = null,
    CancellationToken ct = default)
{
    await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
    
    // 1. Create domain entity
    var romFile = new RomFile {
        FileEntityId = fileEntityId,
        OriginalFilename = originalFilename,
        ImportedAt = DateTime.UtcNow,
        ImportedBy = importedBy,
        SourceArchive = sourceArchive,
        PathInArchive = pathInArchive
    };
    
    _context.RomFiles.Add(romFile);
    await _context.SaveChangesAsync(ct);
    
    // 2. Atomically increment RefCount
    await _storageService.ClaimReferenceAsync(fileEntityId, ct);
    
    await transaction.CommitAsync(ct);
    
    return romFile.Id;
}
```

**Application Layer (Delete):**
```csharp
public async Task DeleteRomFileAsync(int romFileId, CancellationToken ct = default)
{
    await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
    
    // 1. Get the ROM
    var romFile = await _context.RomFiles.FindAsync(romFileId, ct);
    if (romFile == null) throw new NotFoundException();
    
    var fileEntityId = romFile.FileEntityId;
    
    // 2. Delete domain entity (CASCADE deletes UserFileAssociations)
    _context.RomFiles.Remove(romFile);
    await _context.SaveChangesAsync(ct);
    
    // 3. Atomically decrement RefCount
    await _storageService.ReleaseReferenceAsync(fileEntityId, ct);
    
    await transaction.CommitAsync(ct);
    
    // FileEntity with RefCount=0 becomes eligible for GC after 30 days
}
```

---

### 3.4 Canonical Definitions (Immutable)

```sql
CREATE TABLE DatFiles (
  Id INTEGER PRIMARY KEY AUTOINCREMENT,
  SourceFileEntityId INTEGER REFERENCES FileEntities(Id),  -- The uploaded .dat/.xml
  
  Name TEXT NOT NULL,
  Version TEXT,
  Source TEXT,                  -- "No-Intro", "Redump", "TOSEC"
  PlatformId INTEGER REFERENCES Platforms(Id),
  
  -- Lifecycle (ONLY mutable fields)
  IsActive INTEGER NOT NULL DEFAULT 1,
  ReplacedBy INTEGER REFERENCES DatFiles(Id),
  
  -- Immutable metadata
  ImportedAt TEXT NOT NULL,
  Author TEXT,
  Url TEXT,
  Description TEXT,
  GameCount INTEGER,
  RomCount INTEGER
);

CREATE INDEX idx_datfile_active ON DatFiles(IsActive, PlatformId);
CREATE INDEX idx_datfile_source ON DatFiles(Source);

CREATE TABLE DatGames (
  Id INTEGER PRIMARY KEY AUTOINCREMENT,
  DatFileId INTEGER NOT NULL REFERENCES DatFiles(Id) ON DELETE CASCADE,
  TitleId INTEGER REFERENCES Titles(Id) ON DELETE SET NULL,
  
  -- Canonical metadata (IMMUTABLE)
  Name TEXT NOT NULL,
  Description TEXT,
  Year TEXT,
  Manufacturer TEXT,
  Region TEXT,
  Language TEXT,
  Revision TEXT,
  IsBios INTEGER NOT NULL DEFAULT 0,
  
  ImportedAt TEXT NOT NULL
);

CREATE INDEX idx_datgame_datfile ON DatGames(DatFileId, Name);
CREATE INDEX idx_datgame_title ON DatGames(TitleId);

CREATE TABLE DatRoms (
  Id INTEGER PRIMARY KEY AUTOINCREMENT,
  DatGameId INTEGER NOT NULL REFERENCES DatGames(Id) ON DELETE CASCADE,
  
  -- Canonical requirement (IMMUTABLE)
  -- Note: DAT files typically use SHA-1 as primary identifier
  Name TEXT NOT NULL,
  Sha1 BLOB(20),                -- Primary DAT matching hash
  Md5 BLOB(16),
  Crc32 BLOB(4),
  Size INTEGER,
  Status TEXT,                  -- "good", "verified", "nodump"
  
  ImportedAt TEXT NOT NULL
);

CREATE INDEX idx_datrom_sha1 ON DatRoms(Sha1) WHERE Sha1 IS NOT NULL;
CREATE INDEX idx_datrom_md5 ON DatRoms(Md5) WHERE Md5 IS NOT NULL;
CREATE INDEX idx_datrom_crc ON DatRoms(Crc32) WHERE Crc32 IS NOT NULL;
CREATE INDEX idx_datrom_game ON DatRoms(DatGameId);
```

**Immutability Contract:**
- DatGame and DatRom records are **never UPDATE'd**
- Only INSERT (new DAT) or logical soft-delete (DatFile.IsActive = false)
- Physical deletion only via CASCADE when DatFile deleted (rare - only if admin purges old definitions)

---

### 3.5 Association Bridge (Mutable)

**DatRomAssociations:**
```sql
CREATE TABLE DatRomAssociations (
  Id INTEGER PRIMARY KEY AUTOINCREMENT,
  
  -- Immutable identities
  DatRomId INTEGER NOT NULL REFERENCES DatRoms(Id) ON DELETE CASCADE,
  FileEntityId INTEGER NOT NULL REFERENCES FileEntities(Id) ON DELETE CASCADE,
  
  -- Verification result (MUTABLE - recomputed on re-verify)
  ConformanceState INTEGER NOT NULL DEFAULT 0,  -- 0=Unchecked, 1=Verified, 2=Suspect
  Sha1Match INTEGER,                            -- 0=false, 1=true, NULL=N/A
  Md5Match INTEGER,
  Crc32Match INTEGER,
  
  CreatedAt TEXT NOT NULL,
  LastVerifiedAt TEXT,
  
  UNIQUE(DatRomId, FileEntityId)
);

CREATE INDEX idx_assoc_file ON DatRomAssociations(FileEntityId);
CREATE INDEX idx_assoc_datrom ON DatRomAssociations(DatRomId);
CREATE INDEX idx_assoc_conformance ON DatRomAssociations(ConformanceState);
CREATE INDEX idx_assoc_verified ON DatRomAssociations(LastVerifiedAt) 
  WHERE ConformanceState = 1;
```

**CRITICAL: Keep this table lean.** No text columns, no audit JSON, no foreign keys beyond the minimal set. This table will hit millions of rows and is read/written frequently.

**UserFileAssociations:**
```sql
CREATE TABLE UserFileAssociations (
  Id INTEGER PRIMARY KEY AUTOINCREMENT,
  RomFileId INTEGER NOT NULL UNIQUE REFERENCES RomFiles(Id) ON DELETE CASCADE,
  TitleId INTEGER NOT NULL REFERENCES Titles(Id) ON DELETE CASCADE,
  
  -- User-defined version metadata
  CustomVersionName TEXT NOT NULL,
  CustomRegion TEXT,
  CustomRevision TEXT,
  LinkType TEXT NOT NULL,       -- 'hack', 'translation', 'prototype', 'homebrew', 'bad-dump'
  
  -- Optional verification
  ExpectedSha1 BLOB(20),
  ConformanceState INTEGER,     -- Computed if ExpectedSha1 provided
  
  -- Audit
  CreatedBy TEXT,
  CreatedAt TEXT NOT NULL,
  Notes TEXT
);

CREATE INDEX idx_userlink_title ON UserFileAssociations(TitleId);
CREATE INDEX idx_userlink_romfile ON UserFileAssociations(RomFileId);
```

---

### 3.6 Read Models (Performance Cache)

```sql
-- Dashboard statistics (updated after jobs)
CREATE TABLE SystemStats (
  Id INTEGER PRIMARY KEY CHECK (Id = 1),  -- Singleton
  
  -- File counts
  TotalFiles INTEGER NOT NULL,
  VerifiedFiles INTEGER NOT NULL,          -- Has ≥1 Verified association
  SuspectFiles INTEGER NOT NULL,           -- Has ≥1 Suspect association
  UncheckedFiles INTEGER NOT NULL,         -- All associations Unchecked
  UnmatchedFiles INTEGER NOT NULL,         -- Zero associations
  UserLinkedFiles INTEGER NOT NULL,        -- Has UserFileAssociation
  
  -- Title counts
  TotalTitles INTEGER NOT NULL,
  EnrichedTitles INTEGER NOT NULL,
  OwnedTitles INTEGER NOT NULL,
  
  -- Storage
  TotalStorageBytes INTEGER NOT NULL,
  TotalCompressedBytes INTEGER NOT NULL,
  DeduplicationSavingsBytes INTEGER NOT NULL,
  
  LastUpdated TEXT NOT NULL
);

-- Per-platform breakdown
CREATE TABLE PlatformStats (
  PlatformId INTEGER PRIMARY KEY REFERENCES Platforms(Id),
  PlatformName TEXT NOT NULL,
  
  TotalFiles INTEGER NOT NULL,
  VerifiedFiles INTEGER NOT NULL,
  TotalTitles INTEGER NOT NULL,
  OwnedTitles INTEGER NOT NULL,
  
  LastUpdated TEXT NOT NULL
);
```

**Rebuild Strategy:**

```csharp
// Called after: Import jobs, Verify jobs, DAT replacement jobs
public async Task RebuildSystemStatsAsync(CancellationToken ct)
{
    // Single aggregation query (may take 10-30s for 1M files)
    var stats = await _context.Database.SqlQueryRaw<SystemStatsProjection>(@"
        SELECT 
            COUNT(DISTINCT fe.Id) as TotalFiles,
            COUNT(DISTINCT CASE WHEN EXISTS(
                SELECT 1 FROM DatRomAssociations dra 
                WHERE dra.FileEntityId = fe.Id 
                  AND dra.ConformanceState = 1
            ) THEN fe.Id END) as VerifiedFiles,
            COUNT(DISTINCT CASE WHEN EXISTS(
                SELECT 1 FROM DatRomAssociations dra
                WHERE dra.FileEntityId = fe.Id
                  AND dra.ConformanceState = 2
            ) THEN fe.Id END) as SuspectFiles,
            -- ... more aggregations
            SUM(fe.Size) as TotalStorageBytes,
            SUM(COALESCE(fe.CompressedSize, fe.Size)) as TotalCompressedBytes
        FROM FileEntities fe
        JOIN RomFiles rf ON rf.FileEntityId = fe.Id
    ").FirstAsync(ct);
    
    // Upsert into read model
    var systemStats = await _context.SystemStats.FindAsync(1, ct) 
        ?? new SystemStats { Id = 1 };
        
    systemStats.TotalFiles = stats.TotalFiles;
    systemStats.VerifiedFiles = stats.VerifiedFiles;
    // ... update all fields
    systemStats.LastUpdated = DateTime.UtcNow;
    
    if (_context.Entry(systemStats).State == EntityState.Detached)
        _context.SystemStats.Add(systemStats);
        
    await _context.SaveChangesAsync(ct);
}
```

---

## 4. Implementation Roadmap (REVISED)

### **Phase 0: Storage Foundation (Weeks 1-3)**

**Goal:** Implement Romd.Storage blob layer with application-layer RefCount management.

#### Week 1: Blob Layer (IContentAddressableStore)
- [x] Create `Romd.Storage` project (standalone)
- [x] Implement `ContentHash` value object (SHA-256+SHA-1+MD5+CRC32 computation)
- [x] Implement hash value types (`Sha256`, `Sha1`, `Md5`, `Crc32`)
- [x] Implement `StorageKey` value object (sharded path: XX/YY/hash)
- [x] Implement `IContentAddressableStore` interface
- [x] Implement `ContentStoreOptions` with validation

#### Week 2: Physical Storage Implementation
- [x] Implement `FileSystemContentStore`
    - Sharded directories (SHA-256 first 4 hex chars)
    - Transparent zstd compression (level 3)
    - Smart compression skipping (MinCompressionRatio)
    - Atomic write (temp file + rename)
    - Deduplication via hash check
    - Concurrent store coordination
- [x] Implement `HashVerifyingStream` for retrieval integrity
- [x] Implement `BoundedReadStream` for size limits
- [x] Implement `HashingWriteStream` for non-seekable streams
- [x] Unit tests for concurrent operations
- [x] Integration tests for compression decisions

#### Week 3: Application Layer & Migration
- [ ] Add `FileEntities` table to database (SHA-256 primary)
- [ ] Implement `IStorageService` wrapping `IContentAddressableStore`
- [ ] Implement `ClaimReferenceAsync`, `ReleaseReferenceAsync`
- [ ] Migration script: RomFile hash/storage → FileEntity
- [ ] Migration script: TitleMedia storage → FileEntity (as MediaFile)
- [ ] Migration script: DatFile blob → FileEntity
- [ ] Validation: RefCount integrity check (manual count vs DB)
- [ ] Performance test: 100K file migration completes in <5 min
- [ ] Rollback documentation

**Acceptance Criteria:**
- [x] Same file uploaded 3x → 1 blob, detected as deduplicated
- [ ] RomFile deleted → RefCount-- via `ReleaseReferenceAsync`
- [x] Concurrent creates don't corrupt store (stress test: 10 parallel)
- [ ] GC deletes orphans after 30 days
- [ ] Migration: Zero data loss, all hashes verified
- [x] Pre-compressed files stored as .raw (smart compression)
- [x] Retrieval verifies SHA-256 integrity

---

### **Phase 1: Immutable Associations (Weeks 4-6)**

**Goal:** Move verification state to bridge tables.

#### Week 4: Schema
- [ ] Create `DatRomAssociations` table
- [ ] Create `UserFileAssociations` table
- [ ] Create `SystemStats` and `PlatformStats` tables
- [ ] Add `DatFile.IsActive`, `DatFile.ReplacedBy` columns
- [ ] Remove `DatRom.RomFileId` column (breaking change)
- [ ] Remove `RomFile.Conformance/VerifiedAt` columns (breaking change)

#### Week 5: Application Logic
- [ ] Implement association creation on file import
    - Query active DatRoms by SHA-1 hash (DAT matching)
    - Create DatRomAssociation for each match
    - Compute ConformanceState immediately
- [ ] Implement association creation on DAT import
    - Query all FileEntities by SHA-1
    - Match against new DatRoms (incremental)
    - Create associations
- [ ] Implement user linking (creates UserFileAssociation)
- [ ] Update all repositories to query through bridge tables

#### Week 6: Migration & Testing
- [ ] Migrate existing DatRom.RomFileId → DatRomAssociations
- [ ] Migrate existing RomFile.Conformance → association state
- [ ] Build initial SystemStats read model
- [ ] Performance test: Multi-DAT inspector <100ms
- [ ] Integration test: DAT replacement flow

**Acceptance Criteria:**
- [ ] File can have 5 canonical associations (multi-DAT)
- [ ] User link creates separate record (not in DatGame)
- [ ] DAT deactivation preserves old associations (audit trail)
- [ ] Queries filter to `IsActive = 1` by default
- [ ] SystemStats rebuild completes in <30s for 1M files

---

### **Phase 2: Trust & Scale (Weeks 7-10)**

#### Week 7: Read Model Infrastructure
- [ ] Background job: Rebuild SystemStats after import/verify
- [ ] Dashboard migrated to query SystemStats (not live aggregations)
- [ ] PlatformStats for per-platform breakdown
- [ ] Manual "Refresh Stats" button (admin only)

#### Week 8: Server-Side Pagination
- [ ] Cursor pagination on all list endpoints
- [ ] Infinite scroll: Catalog grid (48/page)
- [ ] Infinite scroll: Files list (200/page)
- [ ] SQLite FTS5 virtual tables for search
- [ ] Remove client-side filtering from React

#### Week 9: Visual Trust Signals
- [ ] Monogram fallback generator (server SVG endpoint)
- [ ] "Verification Pending" state (grey) when never scanned
- [ ] Multi-DAT badge: "🛡️ Verified (2 Definitions)"
- [ ] Per-match conformance in Inspector

#### Week 10: Safety & Automation
- [ ] Auto-queue IGDB enrichment on import
- [ ] Rate limiter (4 req/sec Hangfire throttle)
- [ ] Delete confirmation with dependency tree
- [ ] Merge dry-run preview

**Acceptance Criteria:**
- [ ] Dashboard loads <200ms (reads SystemStats)
- [ ] Catalog: 50K games scroll smoothly
- [ ] Multi-DAT files show all matches with distinct states
- [ ] Monograms eliminate broken images

---

### **Phase 3: Workflow Pivot (Weeks 11-14)**

#### Week 11-12: Inbox
- [ ] Split-view layout
- [ ] Smart suggestions (Levenshtein + size)
- [ ] Confidence scoring with reasoning
- [ ] Manual search autocomplete
- [ ] User association creation modal (LinkType selector)
- [ ] Bulk retry match
- [ ] Export hashes (TSV)

#### Week 13: Import Wizard
- [ ] 3-step modal (Upload → Analyze → Commit)
- [ ] Client-side hashing <500MB (fallback to server)
- [ ] Pre-flight summary
- [ ] Background job integration
- [ ] Progress notifications

#### Week 14: Status Unification
- [ ] Semantic icons (Shield, Clock, Warning, Link)
- [ ] Tooltips with timestamps
- [ ] 🔗 "User Linked" badge (blue, distinct from green Verified)
- [ ] Color-blind accessible

**Acceptance Criteria:**
- [ ] Inbox triage: <5 min per 100 files
- [ ] Smart suggestions: >70% acceptance
- [ ] User links visible with distinct styling
- [ ] Import accurate ±5% match prediction

---

### **Phase 4: Curation & Client Readiness (Weeks 15-18)**

#### Week 15-16: Collections
- [ ] Dynamic rules builder
- [ ] Manual overrides with reasons
- [ ] Collection composition (union/intersect)
- [ ] Preview with live count (debounced)
- [ ] Library Views as Collection pointers

#### Week 17: BIOS & Attachments
- [ ] BiosFile using FileEntity
- [ ] Auto-match BIOS by hash
- [ ] MediaFile polymorphic (Game/Platform/Collection)
- [ ] All use Romd.Storage CAS

#### Week 18: Export & Integration
- [ ] Collection → DAT XML export
- [ ] User association export
- [ ] Version download API (single + multi-file ZIP)
- [ ] Client documentation

**Acceptance Criteria:**
- [ ] Collections handle 1M games
- [ ] BIOS auto-match >90%
- [ ] DAT export validates
- [ ] Client version picker works

---

## 5. Technical Specifications

### 5.1 Hash Algorithm Strategy (FINALIZED)

**Dual-Hash Architecture:**
- **SHA-256** for storage identity (content-addressed key)
- **SHA-1** for DAT matching (industry standard)

**Hash Computation:**
All four hashes are computed in a single pass during ingest:

```csharp
public static async ValueTask<ContentHash> ComputeAsync(
    Stream stream,
    IProgress<long>? progress = null,
    CancellationToken ct = default)
{
    using var ctx = new HashComputeContext();
    return await ctx.ComputeAsync(stream, progress, ct);
}

// HashComputeContext uses IncrementalHash for SHA-256, SHA-1, MD5
// and System.IO.Hashing.Crc32 for CRC32
// All computed simultaneously as bytes flow through
```

**Conformance Computation:**
```csharp
public static ConformanceState ComputeConformance(
    FileEntity fileEntity,
    DatRom datRom)
{
    // SHA-1 match is primary for DAT conformance
    var sha1Match = datRom.Sha1 != null
        ? fileEntity.Sha1.SequenceEqual(datRom.Sha1)
        : (bool?)null;
    
    var md5Match = datRom.Md5 != null && fileEntity.Md5 != null
        ? fileEntity.Md5.SequenceEqual(datRom.Md5)
        : (bool?)null;
    
    var crc32Match = datRom.Crc32 != null && fileEntity.Crc32 != null
        ? fileEntity.Crc32.SequenceEqual(datRom.Crc32)
        : (bool?)null;
    
    // All provided hashes must match
    var providedMatches = new[] { sha1Match, md5Match, crc32Match }
        .Where(m => m.HasValue)
        .ToList();
    
    if (providedMatches.Count == 0)
        return ConformanceState.Unchecked;
    
    if (providedMatches.All(m => m == true))
        return ConformanceState.Verified;
    
    if (providedMatches.Any(m => m == false))
        return ConformanceState.Suspect;
    
    return ConformanceState.Unchecked;
}
```

**Collision Detection:**
```csharp
// If SHA-1 matches but size differs → potential collision or bad dump
if (sha1Match == true && fileEntity.Size != datRom.Size)
{
    _logger.LogWarning(
        "Possible SHA-1 collision or size mismatch: FileEntity {FileId} matches DatRom {DatRomId} by hash but size differs ({FileSize} vs {DatSize})",
        fileEntity.Id, datRom.Id, fileEntity.Size, datRom.Size);
        
    // Create association with Suspect state + alert admin
    return ConformanceState.Suspect;
}
```

---

### 5.2 Multi-Title Associations (FINALIZED)

**Decision:** Allow files to associate with multiple Titles. Do not auto-merge.

**Scenario:**
```
FileEntity (SHA-256: abc123..., SHA-1: def456...)
matches via SHA-1:
  • DatRom#1 → DatGame "Protector (USA)" → Title "Protector"
  • DatRom#2 → DatGame "Stargate (USA)" → Title "Stargate"
```

**System creates both associations:**
```sql
DatRomAssociations:
  (DatRomId=1, FileEntityId=1, ConformanceState=Verified)
  (DatRomId=2, FileEntityId=1, ConformanceState=Verified)
```

**Inspector UI:**
```
⚠️ MULTI-TITLE MATCH

This file satisfies Definitions for 2 different Games:

✓ Protector (USA) - Arcade
✓ Stargate (USA) - Arcade

This may indicate:
• Duplicate title entries (consider merging)
• DATs using different naming conventions
• Legitimately shared file (rare)

ACTIONS
[Merge Titles] [Keep Both Associations] [Deactivate One DAT]
```

---

### 5.3 DAT Replacement Strategy (FINALIZED)

**Incremental (Differential) Rebuild**

```csharp
public async Task ReplaceDatAsync(
    int oldDatId, 
    int newDatId, 
    CancellationToken ct)
{
    // 1. Get all DatRoms from new DAT
    var newDatRoms = await _context.DatRoms
        .Where(dr => dr.DatGame.DatFileId == newDatId)
        .Include(dr => dr.DatGame)
        .ToListAsync(ct);
    
    // 2. For each new DatRom, find matching FileEntities by SHA-1
    var batchSize = 1000;
    for (int i = 0; i < newDatRoms.Count; i += batchSize)
    {
        var batch = newDatRoms.Skip(i).Take(batchSize);
        var hashes = batch.Select(dr => dr.Sha1).Where(h => h != null).ToList();
        
        var matchingFiles = await _context.FileEntities
            .Where(fe => hashes.Contains(fe.Sha1))
            .ToListAsync(ct);
        
        // 3. Create associations (SKIP if already exists)
        var associations = new List<DatRomAssociation>();
        
        foreach (var file in matchingFiles)
        {
            var datRoms = batch.Where(dr => dr.Sha1.SequenceEqual(file.Sha1));
            
            foreach (var datRom in datRoms)
            {
                // Check if association already exists
                var exists = await _context.DatRomAssociations
                    .AnyAsync(a => a.DatRomId == datRom.Id && a.FileEntityId == file.Id, ct);
                    
                if (exists) continue;  // Skip - already associated
                
                // Create new association with conformance
                var conformance = ComputeConformance(file, datRom);
                    
                associations.Add(new DatRomAssociation {
                    DatRomId = datRom.Id,
                    FileEntityId = file.Id,
                    ConformanceState = conformance,
                    CreatedAt = DateTime.UtcNow,
                    LastVerifiedAt = DateTime.UtcNow
                });
            }
        }
        
        _context.DatRomAssociations.AddRange(associations);
        await _context.SaveChangesAsync(ct);
    }
    
    // 4. Deactivate old DAT (non-destructive)
    await _context.DatFiles
        .Where(df => df.Id == oldDatId)
        .ExecuteUpdateAsync(s => s
            .SetProperty(df => df.IsActive, false)
            .SetProperty(df => df.ReplacedBy, newDatId), ct);
    
    // Old DatRomAssociations remain but filtered out (df.IsActive = false)
    
    // 5. Rebuild stats
    await _statsService.RebuildSystemStatsAsync(ct);
}
```

**Why Incremental:**
- Only processes DatRoms that don't have associations yet
- Typical DAT update: 10-20% of entries change
- Full rebuild: Delete 2M associations + recreate = hours + DB lock
- Incremental: Create ~200K new associations = minutes

---

## 6. Romd.Storage Architecture (Detailed)

### 6.1 Layer Separation

```
┌─────────────────────────────────────────────────────────────┐
│ APPLICATION LAYER (Romd.Storage.Application) - Future      │
│                                                             │
│ IStorageService                                             │
│ ├─ StoreFileAsync (blob + FileEntity + dedup coordination) │
│ ├─ RetrieveFileAsync (by FileEntityId)                     │
│ ├─ ClaimReferenceAsync (atomic RefCount++)                 │
│ ├─ ReleaseReferenceAsync (atomic RefCount--)               │
│ └─ GarbageCollectAsync (cleanup orphans)                   │
│                                                             │
│ Responsibilities:                                           │
│ • Coordinate blob storage with database                    │
│ • Manage FileEntity lifecycle                              │
│ • Reference counting                                        │
│ • Garbage collection scheduling                             │
└─────────────────────────────────────────────────────────────┘
                          │ uses
                          ▼
┌─────────────────────────────────────────────────────────────┐
│ BLOB LAYER (Romd.Storage) - Implemented                    │
│                                                             │
│ IContentAddressableStore                                    │
│ ├─ StoreAsync (content → StoreResult)                      │
│ ├─ RetrieveAsync (StorageKey → Stream?)                    │
│ ├─ ExistsAsync (StorageKey → bool)                         │
│ ├─ DeleteAsync (StorageKey → bool)                         │
│ └─ GetInfoAsync (StorageKey → ContentInfo?)                │
│                                                             │
│ Responsibilities:                                           │
│ • Content-addressed blob storage                           │
│ • Transparent compression/decompression                    │
│ • Integrity verification on retrieval                      │
│ • Concurrent store coordination                            │
│ • NO database knowledge                                    │
│ • NO reference counting                                    │
└─────────────────────────────────────────────────────────────┘
```

### 6.2 Project Structure

```
Romd.Storage/
├─ Abstractions/
│  ├─ ContentHash.cs              (Value object - all 4 hashes)
│  ├─ ContentInfo.cs              (Metadata about stored content)
│  ├─ ContentStoreOptions.cs      (Configuration with validation)
│  ├─ IContentAddressableStore.cs (Primary interface)
│  ├─ StorageKey.cs               (SHA-256 derived path)
│  └─ StoreResult.cs              (Result of store operation)
│
├─ Exceptions/
│  └─ ContentCorruptedException.cs
│
├─ FileSystem/
│  ├─ BoundedReadStream.cs        (Size limit enforcement)
│  ├─ FileSystemContentStore.cs   (Main implementation)
│  ├─ HashVerifyingStream.cs      (Integrity verification)
│  └─ HashingWriteStream.cs       (Hash computation during write)
│
├─ Hashing/
│  ├─ Crc32.cs                    (32-bit, big-endian storage)
│  ├─ HashExtensions.cs           (ToArray, ToShortHex)
│  ├─ HexConverter.cs             (Parse/TryParse)
│  ├─ IHashValue.cs               (Common interface)
│  ├─ Md5.cs                      (128-bit)
│  ├─ Sha1.cs                     (160-bit)
│  └─ Sha256.cs                   (256-bit, primary identifier)
│
├─ ServiceCollectionExtensions.cs (DI registration)
└─ StoreProgress.cs               (Progress reporting)
```

### 6.3 IContentAddressableStore Contract

```csharp
/// <summary>
///     Content-addressable storage where files are identified by their cryptographic hash.
///     Provides automatic deduplication and transparent compression.
/// </summary>
/// <remarks>
///     Thread-safety: All methods are safe to call concurrently. Concurrent stores of
///     identical content are coordinated to avoid duplicate work.
/// </remarks>
public interface IContentAddressableStore : IAsyncDisposable
{
    /// <summary>
    ///     Stores content and returns computed hashes.
    ///     Automatically deduplicates if content already exists.
    /// </summary>
    Task<StoreResult> StoreAsync(
        Stream content,
        IProgress<StoreProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    ///     Retrieves content by storage key.
    /// </summary>
    /// <param name="key">The storage key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A decompressed, hash-verifying stream, or null if not found. Caller must dispose.</returns>
    /// <remarks>
    ///     The returned stream verifies content integrity (SHA-256) when fully consumed.
    ///     Callers must either read the stream to completion or call ForceVerifyAsync()
    ///     to trigger verification. Early disposal skips verification.
    /// </remarks>
    /// <exception cref="ContentCorruptedException">Hash mismatch detected when stream is fully read.</exception>
    Task<Stream?> RetrieveAsync(StorageKey key, CancellationToken ct = default);

    /// <summary>
    ///     Checks if content exists.
    /// </summary>
    Task<bool> ExistsAsync(StorageKey key, CancellationToken ct = default);

    /// <summary>
    ///     Deletes content. Returns true if deleted, false if not found.
    /// </summary>
    Task<bool> DeleteAsync(StorageKey key, CancellationToken ct = default);

    /// <summary>
    ///     Gets metadata without retrieving content.
    /// </summary>
    Task<ContentInfo?> GetInfoAsync(StorageKey key, CancellationToken ct = default);
}
```

### 6.4 StoreResult Structure

```csharp
/// <summary>
///     Result of a store operation.
/// </summary>
public readonly record struct StoreResult(
    ContentHash Hash,           // All 4 computed hashes
    StorageKey Key,             // SHA-256 derived storage path
    long Size,                  // Uncompressed size
    long CompressedSize,        // Actual disk usage
    bool WasDeduplicated,       // True if content already existed
    bool IsCompressed)          // True if stored as .zst, false if .raw
{
    /// <summary>
    ///     Compression ratio (compressed / uncompressed). Lower is better.
    /// </summary>
    public double CompressionRatio => Size > 0 && IsCompressed 
        ? CompressedSize / (double)Size 
        : 1.0;

    /// <summary>
    ///     Bytes saved by compression.
    /// </summary>
    public long BytesSaved => IsCompressed ? Size - CompressedSize : 0;
}
```

### 6.5 Configuration Options

```csharp
public sealed class ContentStoreOptions
{
    /// <summary>
    ///     Root directory for content storage.
    /// </summary>
    [Required]
    public required string RootPath { get; set; }

    /// <summary>
    ///     Zstd compression level (1-22). Default is 3 (balanced speed/ratio).
    /// </summary>
    [Range(1, 22)]
    public int CompressionLevel { get; set; } = 3;

    /// <summary>
    ///     Buffer size for stream operations. Default is 80KB.
    /// </summary>
    [Range(4096, 10 * 1024 * 1024)]
    public int BufferSize { get; set; } = 81920;

    /// <summary>
    ///     Maximum allowed content size. Default is 4GB.
    ///     Protects against decompression bombs and resource exhaustion.
    /// </summary>
    [Range(1, long.MaxValue)]
    public long MaxContentSize { get; set; } = 4L * 1024 * 1024 * 1024;

    /// <summary>
    ///     File extension for compressed content.
    /// </summary>
    public string CompressedExtension { get; set; } = ".zst";

    /// <summary>
    ///     File extension for uncompressed content.
    /// </summary>
    public string UncompressedExtension { get; set; } = ".raw";

    /// <summary>
    ///     Minimum compression ratio to keep compressed version.
    ///     If compression doesn't achieve at least this ratio, store uncompressed.
    ///     Default 0.95 (5% savings minimum).
    /// </summary>
    [Range(0.0, 1.0)]
    public double MinCompressionRatio { get; set; } = 0.95;
}
```

### 6.6 Safety Features

**Decompression Bomb Protection:**
```csharp
// BoundedReadStream enforces MaxContentSize during read
// Applied to both store (non-seekable streams) and retrieve operations

stream = new BoundedReadStream(stream, _options.MaxContentSize, false);
// Throws InvalidOperationException if content exceeds limit
```

**Integrity Verification on Retrieval:**
```csharp
// HashVerifyingStream computes SHA-256 as data is read
// Throws ContentCorruptedException on mismatch when fully consumed

stream = new HashVerifyingStream(stream, key, _logger);

// Callers must either:
// 1. Read stream to completion, OR
// 2. Call ForceVerifyAsync() to verify without processing all content
```

**Smart Compression:**
```csharp
// After compression, check if it was worthwhile
double ratio = compressedSize / (double)uncompressedSize;

if (ratio <= _options.MinCompressionRatio)
{
    // Keep compressed version (.zst)
    File.Move(compressedTempPath, finalPath);
}
else
{
    // Compression not beneficial, store raw (.raw)
    File.Delete(compressedTempPath);
    File.Move(uncompressedTempPath, finalPath);
}
```

**Concurrent Store Coordination:**
```csharp
// ConcurrentDictionary tracks in-flight stores by SHA-256
// Prevents duplicate work when same content uploaded simultaneously

if (_inFlightStores.TryAdd(hash.Sha256, operation))
{
    // We're the winner - perform the store
    try { ... }
    finally { _inFlightStores.TryRemove(hash.Sha256, out _); }
}
else
{
    // Another caller is storing - wait for their result
    return await existing.Task.WaitAsync(ct);
}
```

### 6.7 Progress Reporting

```csharp
public enum StorePhase
{
    Hashing,      // Computing content hash
    Compressing,  // Compressing content
    Writing,      // Writing to storage (uncompressed path)
    Complete      // Operation complete
}

public readonly record struct StoreProgress
{
    public required StorePhase Phase { get; init; }
    public required long BytesProcessed { get; init; }
    public long? TotalBytes { get; init; }  // Null for non-seekable streams
    
    public double? PercentComplete => TotalBytes > 0
        ? Math.Min(100.0, BytesProcessed * 100.0 / TotalBytes.Value)
        : null;
}
```

---

## 7. Scope Boundaries (v2.4 Explicit Exclusions)

### ✅ IN SCOPE

**Supported File Types:**
- Single-file ROMs (NES, SNES, Genesis, GBA, etc.)
- Single-file disc images (PSX ISO, Dreamcast CDI, N64 ROM)
- Multi-disc games (each disc as separate FileEntity)
- Archive extraction (ZIP, 7z, RAR → individual files)
- BIOS files (single binaries)
- Media assets (Cover images, Screenshots, PDFs)
- DAT files (XML/DAT manifests)

**Supported Platforms:**
- Cartridge-based systems (all eras)
- CD/DVD-based systems (PSX, PS2, Dreamcast, GameCube via ISO)
- Arcade (MAME ROMs as single files)

---

### ❌ OUT OF SCOPE (Deferred to v3.x)

**Folder-Based Games:**
- PS3 PKG decrypted dumps (10,000+ files per game)
- WiiU/Switch decrypted games (folder structure)
- PC game installations (EXE + DLLs + assets)

**Rationale:**
- FileEntity model assumes 1 file = 1 entity
- Folder games would create 10K FileEntity records per game
- Requires "Composite File" or "Manifest" abstraction
- Adds significant complexity to verification logic

**Workaround for v2.4:**
- Store folder games as TAR/ZIP archives
- Do not extract (treat archive as single blob)
- Badge: "Archive (not verified)" - cannot verify individual files

---

**Multi-Tier Storage:**
- Hot SSD / Cold HDD tiering
- Automatic migration based on access frequency

**Rationale:**
- Adds storage backend complexity
- 2TB SSD sufficient for v2.4 target scale
- Can be added in v3.x if needed

---

**Cloud Storage:**
- S3, Azure Blob, Google Cloud Storage

**Rationale:**
- Local FileSystem sufficient for initial deployments
- Cloud support requires different performance characteristics
- Can be added via IContentAddressableStore implementation in v3.x

---

## 8. Performance Architecture

### 8.1 SQLite Concurrency Strategy

**Constraint:** SQLite allows 1 writer at a time (WAL mode = concurrent reads OK).

**Solution:**

**Phase 1: Hash Computation (Parallel)**
```csharp
// 8 parallel threads compute hashes (CPU + I/O bound)
var hashTasks = files.Select(async file => {
    await using var stream = File.OpenRead(file.Path);
    return await ContentHash.ComputeAsync(stream);
});

var hashes = await Task.WhenAll(hashTasks);
// No DB writes yet - purely computational
```

**Phase 2: Database Writes (Serialized)**
```csharp
// Single-threaded queue processes results
var writeLock = new SemaphoreSlim(1, 1);

foreach (var (file, hash) in files.Zip(hashes))
{
    await writeLock.WaitAsync();
    try
    {
        await using var transaction = _context.Database.BeginTransactionAsync();
        
        // Store file via IStorageService
        var result = await _storageService.StoreFileAsync(stream);
        
        // Create RomFile
        var romFile = new RomFile { FileEntityId = result.FileEntityId, ... };
        _context.RomFiles.Add(romFile);
        await _context.SaveChangesAsync();
        
        // Claim reference (atomic update)
        await _storageService.ClaimReferenceAsync(result.FileEntityId);
        
        await transaction.CommitAsync();
    }
    finally
    {
        writeLock.Release();
    }
}
```

**EF Core Configuration:**
```csharp
services.AddDbContext<AppDbContext>(options =>
{
    options.UseSqlite(connectionString, sqlite => {
        sqlite.CommandTimeout(300);  // 5 minute timeout for long queries
        sqlite.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
    });
});
```

---

### 8.2 Query Performance Targets

| Operation | Target | Strategy |
|:---|:---|:---|
| File import + hash | <2s per file | Parallel hashing (8 workers) |
| DAT match query (SHA-1) | <50ms | Indexed hash lookups |
| Association creation | <10ms/file | Batched inserts (100 per transaction) |
| Files list (200 items) | <150ms | Cursor pagination + indexes |
| Multi-DAT inspector | <100ms | JOIN through bridge table (6-way) |
| Dashboard load | <200ms | Read from SystemStats (no joins) |
| DAT replacement | <5min per 100K files | Incremental (differential) rebuild |
| Content retrieval | <50ms + I/O | Direct file access + zstd decompress |

---

## 9. Migration Strategy

### Phase 0: Storage Foundation (3 weeks)

**Critical Path: Blob Layer Complete, Application Layer Next**

#### Week 1-2: Blob Layer (COMPLETE)
- [x] Create Romd.Storage project
- [x] Implement hash value types with optimized equality
- [x] Implement ContentHash with parallel computation
- [x] Implement StorageKey with sharding logic
- [x] Implement IContentAddressableStore interface
- [x] Implement FileSystemContentStore
- [x] Implement safety streams (Bounded, HashVerifying, HashingWrite)
- [x] Unit tests for concurrent operations
- [x] Smart compression with MinCompressionRatio

#### Week 3: Application Layer & Migration
- [ ] Add FileEntities table (SHA-256 primary, SHA-1 indexed)
- [ ] Implement IStorageService wrapping IContentAddressableStore
- [ ] Implement ClaimReferenceAsync/ReleaseReferenceAsync
- [ ] Migration: RomFile → FileEntity extraction
- [ ] Migration: Validate all hashes
- [ ] Performance: 100K migration <5 min

**Acceptance:**
- [x] Deduplication works (same content → same blob)
- [x] Smart compression (pre-compressed files stored as .raw)
- [x] Integrity verification on retrieval
- [x] Concurrent stress test passes
- [ ] RefCount operations atomic
- [ ] Migration zero data loss

---

## 10. API Contracts (Complete)

### 10.1 File Inspector (Multi-Association)

```typescript
GET /api/files/{fileId}/associations
Response: {
  fileIdentity: {
    id: number,
    sha256: string,               // Primary content identifier
    sha1: string,                 // DAT matching hash
    md5: string,
    crc32: string,
    size: number,                 // Uncompressed
    compressedSize: number,
    isCompressed: boolean,
    storagePath: string,
    refCount: number,
    createdAt: string
  },
  
  domainContexts: {
    romFiles: {
      id: number,
      originalFilename: string,
      importedAt: string,
      sourceArchive: string | null,
      pathInArchive: string | null
    }[],
    biosFiles: BiosFileInfo[],
    mediaFiles: MediaFileInfo[]
  },
  
  canonicalMatches: {
    associationId: number,
    
    // DAT info
    datId: number,
    datName: string,
    datVersion: string,
    datSource: string,
    isActiveDat: boolean,
    
    // Game info
    gameId: number,
    gameName: string,
    region: string | null,
    revision: string | null,
    titleId: number,
    titleName: string,
    
    // Verification state (matched via SHA-1)
    conformanceState: 'Verified' | 'Suspect' | 'Unchecked',
    sha1Match: boolean,
    md5Match: boolean | null,
    crc32Match: boolean | null,
    lastVerifiedAt: string,
    
    // Expected values from DAT
    expectedSha1: string,
    expectedMd5: string | null,
    expectedCrc32: string | null,
    expectedSize: number
  }[],
  
  userAssociations: {
    associationId: number,
    titleId: number,
    titleName: string,
    customVersionName: string,
    customRegion: string | null,
    linkType: 'hack' | 'translation' | 'prototype' | 'homebrew' | 'bad-dump',
    
    expectedSha1: string | null,
    conformanceState: 'UserVerified' | 'UserSuspect' | 'UserLinked',
    
    createdBy: string,
    createdAt: string,
    notes: string
  }[],
  
  statusSummary: {
    isCataloged: boolean,
    canonicalMatchCount: number,
    verifiedMatchCount: number,
    suspectMatchCount: number,
    userLinkCount: number,
    inactiveMatchCount: number
  }
}
```

---

### 10.2 Manual Linking (User Association)

```typescript
POST /api/files/{fileId}/link
Body: {
  titleId: number,
  customVersionName: string,
  linkType: 'hack' | 'translation' | 'prototype' | 'homebrew' | 'bad-dump',
  customRegion?: string,
  customRevision?: string,
  expectedSha1?: string,            // Optional: User asserts hash
  notes?: string
}

Response: {
  associationId: number,
  conformanceState: 'UserLinked' | 'UserVerified' | 'UserSuspect',
  statusChange: {
    before: 'Unmatched',
    after: 'Cataloged'
  }
}

Behavior:
  1. Create UserFileAssociation record
  2. Does NOT modify DatGame/DatRom tables
  3. If expectedSha1 provided:
     - Get FileEntity.Sha1
     - Compute match: sha1 === expectedSha1
     - Set UserVerified or UserSuspect
  4. If expectedSha1 omitted:
     - Set UserLinked (neutral)
  5. File removed from Inbox (has association now)
```

---

## 11. Success Metrics

### Phase 0: Storage Foundation
- **Data Integrity:** 100% SHA-256 verification on retrieval
- **Deduplication:** ≥5% file count reduction
- **Compression:** ≥40% storage savings on compressible content
- **Smart Compression:** Pre-compressed files stored as .raw
- **RefCount Accuracy:** Stress test (100 parallel) = correct counts
- **GC Correctness:** Zero false deletions

### Phase 1: Immutable Associations
- **Multi-DAT:** Files with 3+ associations display correctly
- **Query Performance:** Inspector <100ms for 5 matches
- **DAT Replacement:** 100K re-association in <5 min
- **Read Model:** Dashboard <200ms (vs >2s before)

### Phase 2: Trust & Scale
- **User Perception:** Zero "broken image" complaints
- **Performance:** 50K catalog <1.5s load
- **Accuracy:** Stats match manual audit

### Phase 3: Workflows
- **Speed:** Inbox 100 files in <10 min
- **Accuracy:** Suggestions >70% acceptance
- **Adoption:** User links >50% of manual triage

### Phase 4: Curation
- **Community:** ≥10 Collections in 30 days
- **Integration:** ≥3 client apps
- **Export:** ≥5 shared DATs

---

## 12. Architecture Signoff

**Approved Patterns:**
- ✅ Trinity (Asset/Definition/Association)
- ✅ Immutability on canonical entities
- ✅ CAS via Romd.Storage with SHA-256 identity
- ✅ SHA-1 for DAT matching (industry standard)
- ✅ Application-layer atomic RefCount (NO SQL triggers)
- ✅ Read models for Dashboard performance
- ✅ Incremental DAT replacement
- ✅ Multi-DAT matching
- ✅ Smart compression with MinCompressionRatio
- ✅ Integrity verification on retrieval

**Critical Constraints:**
- ⚠️ Decompress archives on ingest (store individual files)
- ⚠️ RefCount via `ExecuteUpdateAsync` in transactions
- ⚠️ Read models for Dashboard (not live aggregations)
- ⚠️ Serialize DB writes (SQLite 1-writer limit)
- ⚠️ Single-file ROMs only (no folder-based games)
- ⚠️ MaxContentSize enforced (default 4GB)

**Ready For:**
- Sprint 1: IStorageService implementation
- Database: FileEntity schema migration
- DevOps: Storage infrastructure (2TB SSD, backup strategy)

---

**END OF SPECIFICATION v2.4**

This is the **final architectural blueprint**. All implementation decisions reference this document. Folder-based games deferred to v3.0 with separate specification.

---

## Appendix A: Hash Type Quick Reference

| Type | Bytes | Hex Length | Purpose |
|------|-------|------------|---------|
| SHA-256 | 32 | 64 | Storage identity, StorageKey derivation |
| SHA-1 | 20 | 40 | DAT matching (No-Intro, Redump, TOSEC) |
| MD5 | 16 | 32 | Legacy DAT compatibility |
| CRC32 | 4 | 8 | Legacy DAT compatibility, quick checks |

**CRC32 Endianness Note:**
- DAT files represent CRC32 as big-endian hex (e.g., "ABCD1234")
- System.IO.Hashing.Crc32 outputs little-endian bytes
- `Crc32.FromLittleEndian()` handles conversion automatically

---

## Appendix B: Storage Path Examples

```
Content SHA-256: abcd1234ef567890...
Storage Key:     ab/cd/abcd1234ef567890...

Physical paths:
  /storage/ab/cd/abcd1234ef567890....zst  (compressed ROM)
  /storage/ab/cd/abcd1234ef567890....raw  (uncompressed, e.g., ZIP file)

Path derivation:
  1. Compute SHA-256 of content
  2. Convert to lowercase hex (64 characters)
  3. Shard: first 2 chars / next 2 chars / full hash
  4. Append extension based on compression decision
```
