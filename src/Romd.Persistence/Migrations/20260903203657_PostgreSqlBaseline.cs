using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using NpgsqlTypes;

#nullable disable

namespace Romd.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PostgreSqlBaseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "romd");

            migrationBuilder.CreateTable(
                name: "AdminRealtimeOutboxEvents",
                schema: "romd",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EventType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    SchemaVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    CreatedAtUtc = table.Column<long>(type: "bigint", nullable: false),
                    AvailableAtUtc = table.Column<long>(type: "bigint", nullable: false),
                    ProcessedAtUtc = table.Column<long>(type: "bigint", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ClaimId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ClaimedAtUtc = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminRealtimeOutboxEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoles",
                schema: "romd",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CatalogSources",
                schema: "romd",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false, defaultValue: "Active"),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogSources", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Files",
                schema: "romd",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Sha256 = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    SizeOnDisk = table.Column<long>(type: "bigint", nullable: false),
                    IsCompressed = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Files", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GameLanguages",
                schema: "romd",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Code = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsAutoCreated = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameLanguages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Libraries",
                schema: "romd",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ConfigurationJson = table.Column<string>(type: "TEXT", nullable: false),
                    ConfigurationState = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false, defaultValue: "Valid"),
                    ConfigurationError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    NeedsMaterialization = table.Column<bool>(type: "boolean", nullable: false),
                    MaterializationGeneration = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    LastMaterializedAt = table.Column<long>(type: "bigint", nullable: true),
                    ItemCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedAt = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Libraries", x => x.Id);
                    table.CheckConstraint("CK_Libraries_MaterializationGeneration", "\"MaterializationGeneration\" >= 0");
                });

            migrationBuilder.CreateTable(
                name: "OpenIddictApplications",
                schema: "romd",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    ApplicationType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ClientId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ClientSecret = table.Column<string>(type: "text", nullable: true),
                    ClientType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ConcurrencyToken = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ConsentType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    DisplayName = table.Column<string>(type: "text", nullable: true),
                    DisplayNames = table.Column<string>(type: "text", nullable: true),
                    JsonWebKeySet = table.Column<string>(type: "text", nullable: true),
                    Permissions = table.Column<string>(type: "text", nullable: true),
                    PostLogoutRedirectUris = table.Column<string>(type: "text", nullable: true),
                    Properties = table.Column<string>(type: "text", nullable: true),
                    RedirectUris = table.Column<string>(type: "text", nullable: true),
                    Requirements = table.Column<string>(type: "text", nullable: true),
                    Settings = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenIddictApplications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OpenIddictScopes",
                schema: "romd",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    ConcurrencyToken = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Descriptions = table.Column<string>(type: "text", nullable: true),
                    DisplayName = table.Column<string>(type: "text", nullable: true),
                    DisplayNames = table.Column<string>(type: "text", nullable: true),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Properties = table.Column<string>(type: "text", nullable: true),
                    Resources = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenIddictScopes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Platforms",
                schema: "romd",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ShortName = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Manufacturer = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CatalogRebuildState = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    CatalogRebuiltAt = table.Column<long>(type: "bigint", nullable: true),
                    CatalogRebuildError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CatalogRebuildFailedAtUtc = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Platforms", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Regions",
                schema: "romd",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsAutoCreated = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Regions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoleClaims",
                schema: "romd",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    ClaimValue = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoleClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetRoleClaims_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalSchema: "romd",
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DatSources",
                schema: "romd",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CatalogSourceId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DatSources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DatSources_CatalogSources_CatalogSourceId",
                        column: x => x.CatalogSourceId,
                        principalSchema: "romd",
                        principalTable: "CatalogSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RomFiles",
                schema: "romd",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OriginalFilename = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    FileId = table.Column<int>(type: "integer", nullable: false),
                    Sha1 = table.Column<byte[]>(type: "bytea", maxLength: 20, nullable: false),
                    Md5 = table.Column<byte[]>(type: "bytea", maxLength: 16, nullable: false),
                    Crc32 = table.Column<byte[]>(type: "bytea", maxLength: 4, nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RomFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RomFiles_Files_FileId",
                        column: x => x.FileId,
                        principalSchema: "romd",
                        principalTable: "Files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GameLanguageAliases",
                schema: "romd",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    GameLanguageId = table.Column<int>(type: "integer", nullable: false),
                    NormalizedAlias = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameLanguageAliases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GameLanguageAliases_GameLanguages_GameLanguageId",
                        column: x => x.GameLanguageId,
                        principalSchema: "romd",
                        principalTable: "GameLanguages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUsers",
                schema: "romd",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LibraryId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<long>(type: "bigint", nullable: true),
                    UserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    EmailConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: true),
                    SecurityStamp = table.Column<string>(type: "text", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "text", nullable: true),
                    PhoneNumber = table.Column<string>(type: "text", nullable: true),
                    PhoneNumberConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    LockoutEnd = table.Column<long>(type: "bigint", nullable: true),
                    LockoutEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUsers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetUsers_Libraries_LibraryId",
                        column: x => x.LibraryId,
                        principalSchema: "romd",
                        principalTable: "Libraries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "OpenIddictAuthorizations",
                schema: "romd",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    ApplicationId = table.Column<string>(type: "text", nullable: true),
                    ConcurrencyToken = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CreationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Properties = table.Column<string>(type: "text", nullable: true),
                    Scopes = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Subject = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    Type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenIddictAuthorizations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OpenIddictAuthorizations_OpenIddictApplications_Application~",
                        column: x => x.ApplicationId,
                        principalSchema: "romd",
                        principalTable: "OpenIddictApplications",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Bios",
                schema: "romd",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PlatformId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Bios", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Bios_Platforms_PlatformId",
                        column: x => x.PlatformId,
                        principalSchema: "romd",
                        principalTable: "Platforms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlatformAliases",
                schema: "romd",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PlatformId = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Value = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    NormalizedValue = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformAliases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlatformAliases_Platforms_PlatformId",
                        column: x => x.PlatformId,
                        principalSchema: "romd",
                        principalTable: "Platforms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlatformFieldDefaults",
                schema: "romd",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PlatformId = table.Column<int>(type: "integer", nullable: false),
                    FieldName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SourceId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedAt = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformFieldDefaults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlatformFieldDefaults_Platforms_PlatformId",
                        column: x => x.PlatformId,
                        principalSchema: "romd",
                        principalTable: "Platforms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SourceEntries",
                schema: "romd",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CatalogSourceId = table.Column<int>(type: "integer", nullable: false),
                    EntryKey = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    PlatformId = table.Column<int>(type: "integer", nullable: true),
                    HasLocalPayload = table.Column<bool>(type: "boolean", nullable: false),
                    LastReconcileRunId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SourceEntries_CatalogSources_CatalogSourceId",
                        column: x => x.CatalogSourceId,
                        principalSchema: "romd",
                        principalTable: "CatalogSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SourceEntries_Platforms_PlatformId",
                        column: x => x.PlatformId,
                        principalSchema: "romd",
                        principalTable: "Platforms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Titles",
                schema: "romd",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PlatformId = table.Column<int>(type: "integer", nullable: false),
                    HasLocalPayload = table.Column<bool>(type: "boolean", nullable: false),
                    Name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    SearchDocument = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    SearchVector = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: false, computedColumnSql: "to_tsvector('simple', \"SearchDocument\")", stored: true),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Publisher = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Developer = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Genre = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ReleaseDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Players = table.Column<int>(type: "integer", nullable: true),
                    Rating = table.Column<double>(type: "double precision", nullable: true),
                    ConservativeMinimumAge = table.Column<int>(type: "integer", nullable: true),
                    EnrichmentStatus = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    LastEnrichedAt = table.Column<long>(type: "bigint", nullable: true),
                    CatalogState = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    FieldProvenanceJson = table.Column<string>(type: "TEXT", nullable: false),
                    FieldSourceOverridesJson = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "{}"),
                    ScreenshotPrefsJson = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "{}"),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedAt = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Titles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Titles_Platforms_PlatformId",
                        column: x => x.PlatformId,
                        principalSchema: "romd",
                        principalTable: "Platforms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RegionAliases",
                schema: "romd",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RegionId = table.Column<int>(type: "integer", nullable: false),
                    NormalizedAlias = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegionAliases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RegionAliases_Regions_RegionId",
                        column: x => x.RegionId,
                        principalSchema: "romd",
                        principalTable: "Regions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DatFiles",
                schema: "romd",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Version = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Author = table.Column<string>(type: "text", nullable: true),
                    Url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    PlatformId = table.Column<int>(type: "integer", nullable: true),
                    OriginalFilename = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    FileId = table.Column<int>(type: "integer", nullable: false),
                    GameCount = table.Column<int>(type: "integer", nullable: false),
                    RomCount = table.Column<int>(type: "integer", nullable: false),
                    DiskCount = table.Column<int>(type: "integer", nullable: false),
                    DatSourceId = table.Column<int>(type: "integer", nullable: false),
                    Lifecycle = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "Active"),
                    SupersededAt = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedAt = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DatFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DatFiles_DatSources_DatSourceId",
                        column: x => x.DatSourceId,
                        principalSchema: "romd",
                        principalTable: "DatSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DatFiles_Files_FileId",
                        column: x => x.FileId,
                        principalSchema: "romd",
                        principalTable: "Files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DatFiles_Platforms_PlatformId",
                        column: x => x.PlatformId,
                        principalSchema: "romd",
                        principalTable: "Platforms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserClaims",
                schema: "romd",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    ClaimValue = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetUserClaims_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalSchema: "romd",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserLogins",
                schema: "romd",
                columns: table => new
                {
                    LoginProvider = table.Column<string>(type: "text", nullable: false),
                    ProviderKey = table.Column<string>(type: "text", nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "text", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserLogins", x => new { x.LoginProvider, x.ProviderKey });
                    table.ForeignKey(
                        name: "FK_AspNetUserLogins_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalSchema: "romd",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserRoles",
                schema: "romd",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalSchema: "romd",
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalSchema: "romd",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserTokens",
                schema: "romd",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    LoginProvider = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserTokens", x => new { x.UserId, x.LoginProvider, x.Name });
                    table.ForeignKey(
                        name: "FK_AspNetUserTokens_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalSchema: "romd",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ConsumerUserSettings",
                schema: "romd",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Theme = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    PreferredRegionIdsJson = table.Column<string>(type: "TEXT", nullable: false),
                    PreferredLanguageIdsJson = table.Column<string>(type: "TEXT", nullable: false),
                    RevisionPreference = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConsumerUserSettings", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_ConsumerUserSettings_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalSchema: "romd",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OpenIddictTokens",
                schema: "romd",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    ApplicationId = table.Column<string>(type: "text", nullable: true),
                    AuthorizationId = table.Column<string>(type: "text", nullable: true),
                    ConcurrencyToken = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CreationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExpirationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Payload = table.Column<string>(type: "text", nullable: true),
                    Properties = table.Column<string>(type: "text", nullable: true),
                    RedemptionDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReferenceId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Subject = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    Type = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenIddictTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OpenIddictTokens_OpenIddictApplications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalSchema: "romd",
                        principalTable: "OpenIddictApplications",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_OpenIddictTokens_OpenIddictAuthorizations_AuthorizationId",
                        column: x => x.AuthorizationId,
                        principalSchema: "romd",
                        principalTable: "OpenIddictAuthorizations",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "CatalogReleases",
                schema: "romd",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PlatformId = table.Column<int>(type: "integer", nullable: false),
                    CatalogTitleId = table.Column<int>(type: "integer", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    PrimarySha1 = table.Column<byte[]>(type: "bytea", maxLength: 20, nullable: true),
                    Name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Region = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Language = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Revision = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SourceCount = table.Column<int>(type: "integer", nullable: false),
                    HasTitleConflict = table.Column<bool>(type: "boolean", nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    FileCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedAt = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogReleases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CatalogReleases_Platforms_PlatformId",
                        column: x => x.PlatformId,
                        principalSchema: "romd",
                        principalTable: "Platforms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CatalogReleases_Titles_CatalogTitleId",
                        column: x => x.CatalogTitleId,
                        principalSchema: "romd",
                        principalTable: "Titles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Jobs",
                schema: "romd",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CorrelationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceFilename = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    PlatformId = table.Column<int>(type: "integer", nullable: true),
                    Phase = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    HangfireJobId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CurrentItem = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ErrorsJson = table.Column<string>(type: "TEXT", nullable: false),
                    LastAttemptError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    LastAttemptErrorTruncated = table.Column<bool>(type: "boolean", nullable: false),
                    StartedAt = table.Column<long>(type: "bigint", nullable: true),
                    CompletedAt = table.Column<long>(type: "bigint", nullable: true),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false),
                    ArchivedAt = table.Column<long>(type: "bigint", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<long>(type: "bigint", nullable: false),
                    ExecutionFenceToken = table.Column<Guid>(type: "uuid", nullable: true),
                    ExecutionLeaseExpiresAtUtc = table.Column<long>(type: "bigint", nullable: true),
                    JobType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Scope = table.Column<string>(type: "text", nullable: true),
                    TotalTitles = table.Column<int>(type: "integer", nullable: true),
                    ProcessedCount = table.Column<int>(type: "integer", nullable: true),
                    EnrichedCount = table.Column<int>(type: "integer", nullable: true),
                    NotFoundCount = table.Column<int>(type: "integer", nullable: true),
                    FailedCount = table.Column<int>(type: "integer", nullable: true),
                    SkippedCount = table.Column<int>(type: "integer", nullable: true),
                    TitleId = table.Column<int>(type: "integer", nullable: true),
                    ExportScopeKind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    LibraryId = table.Column<int>(type: "integer", nullable: true),
                    AuthorizedMaterializationGeneration = table.Column<long>(type: "bigint", nullable: true),
                    ExportTotalTitles = table.Column<int>(type: "integer", nullable: true),
                    ExportProcessedTitles = table.Column<int>(type: "integer", nullable: true),
                    TotalFiles = table.Column<int>(type: "integer", nullable: true),
                    ProcessedFiles = table.Column<int>(type: "integer", nullable: true),
                    SkippedFiles = table.Column<int>(type: "integer", nullable: true),
                    ExportPath = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    MaterializationJobEntity_TotalTitles = table.Column<int>(type: "integer", nullable: true),
                    MaterializationJobEntity_ProcessedCount = table.Column<int>(type: "integer", nullable: true),
                    IncludedCount = table.Column<int>(type: "integer", nullable: true),
                    ExcludedCount = table.Column<int>(type: "integer", nullable: true),
                    ExistingDatId = table.Column<int>(type: "integer", nullable: true),
                    NewDatId = table.Column<int>(type: "integer", nullable: true),
                    DatsDiscovered = table.Column<int>(type: "integer", nullable: true),
                    RomsDiscovered = table.Column<int>(type: "integer", nullable: true),
                    DatsProcessed = table.Column<int>(type: "integer", nullable: true),
                    DatsSucceeded = table.Column<int>(type: "integer", nullable: true),
                    RomsProcessed = table.Column<int>(type: "integer", nullable: true),
                    RomsIngested = table.Column<int>(type: "integer", nullable: true),
                    RomsDeduplicated = table.Column<int>(type: "integer", nullable: true),
                    RomsRejected = table.Column<int>(type: "integer", nullable: true),
                    MaxParallelRoms = table.Column<int>(type: "integer", nullable: true),
                    AllowUnidentified = table.Column<bool>(type: "boolean", nullable: true),
                    ArchiveOnly = table.Column<bool>(type: "boolean", nullable: true),
                    ImportSourcePath = table.Column<string>(type: "text", nullable: true),
                    ImportMove = table.Column<bool>(type: "boolean", nullable: true),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Jobs", x => x.Id);
                    table.CheckConstraint("CK_Jobs_ExportScope", "\"JobType\" <> 'export' OR \"ExportScopeKind\" IS NULL OR (\"ExportScopeKind\" = 'Library' AND \"LibraryId\" IS NOT NULL AND \"LibraryId\" > 0 AND \"AuthorizedMaterializationGeneration\" IS NOT NULL AND \"AuthorizedMaterializationGeneration\" >= 0) OR (\"ExportScopeKind\" = 'AllCatalog' AND \"LibraryId\" IS NULL AND \"AuthorizedMaterializationGeneration\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_Jobs_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalSchema: "romd",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Jobs_Platforms_PlatformId",
                        column: x => x.PlatformId,
                        principalSchema: "romd",
                        principalTable: "Platforms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Jobs_Titles_TitleId",
                        column: x => x.TitleId,
                        principalSchema: "romd",
                        principalTable: "Titles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MaterializedLibraryReleases",
                schema: "romd",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LibraryId = table.Column<int>(type: "integer", nullable: false),
                    TitleId = table.Column<int>(type: "integer", nullable: false),
                    CatalogReleaseId = table.Column<int>(type: "integer", nullable: true),
                    DatGameId = table.Column<int>(type: "integer", nullable: false),
                    DatFileId = table.Column<int>(type: "integer", nullable: false),
                    PlatformId = table.Column<int>(type: "integer", nullable: false),
                    IsEligible = table.Column<bool>(type: "boolean", nullable: false),
                    IsComplete = table.Column<bool>(type: "boolean", nullable: false),
                    IsOwned = table.Column<bool>(type: "boolean", nullable: false),
                    IsPlayable = table.Column<bool>(type: "boolean", nullable: false),
                    IsBlocked = table.Column<bool>(type: "boolean", nullable: false),
                    BlockReason = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    IsExposed = table.Column<bool>(type: "boolean", nullable: false),
                    ExposureReason = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaterializedLibraryReleases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MaterializedLibraryReleases_Libraries_LibraryId",
                        column: x => x.LibraryId,
                        principalSchema: "romd",
                        principalTable: "Libraries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MaterializedLibraryReleases_Titles_TitleId",
                        column: x => x.TitleId,
                        principalSchema: "romd",
                        principalTable: "Titles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MaterializedLibraryTitles",
                schema: "romd",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LibraryId = table.Column<int>(type: "integer", nullable: false),
                    TitleId = table.Column<int>(type: "integer", nullable: false),
                    PlatformId = table.Column<int>(type: "integer", nullable: false),
                    Genre = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    IsVisible = table.Column<bool>(type: "boolean", nullable: false),
                    IsOwned = table.Column<bool>(type: "boolean", nullable: false),
                    IsPlayable = table.Column<bool>(type: "boolean", nullable: false),
                    EligibleReleaseCount = table.Column<int>(type: "integer", nullable: false),
                    PlayableReleaseCount = table.Column<int>(type: "integer", nullable: false),
                    ExposedReleaseCount = table.Column<int>(type: "integer", nullable: false),
                    Availability = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaterializedLibraryTitles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MaterializedLibraryTitles_Libraries_LibraryId",
                        column: x => x.LibraryId,
                        principalSchema: "romd",
                        principalTable: "Libraries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MaterializedLibraryTitles_Titles_TitleId",
                        column: x => x.TitleId,
                        principalSchema: "romd",
                        principalTable: "Titles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TitleContentRatings",
                schema: "romd",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TitleId = table.Column<int>(type: "integer", nullable: false),
                    Board = table.Column<int>(type: "integer", nullable: false),
                    Code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Designation = table.Column<int>(type: "integer", nullable: false),
                    MinimumAge = table.Column<int>(type: "integer", nullable: true),
                    DescriptorsJson = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "[]"),
                    Synopsis = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    SourceId = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ExternalRatingId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedAt = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TitleContentRatings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TitleContentRatings_Titles_TitleId",
                        column: x => x.TitleId,
                        principalSchema: "romd",
                        principalTable: "Titles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TitleExternalIds",
                schema: "romd",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TitleId = table.Column<int>(type: "integer", nullable: false),
                    Provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    MatchConfidence = table.Column<float>(type: "real", nullable: false),
                    IsConfirmed = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedAt = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TitleExternalIds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TitleExternalIds_Titles_TitleId",
                        column: x => x.TitleId,
                        principalSchema: "romd",
                        principalTable: "Titles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TitleMedia",
                schema: "romd",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TitleId = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    FileId = table.Column<int>(type: "integer", nullable: false),
                    SourceId = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    IsPrimary = table.Column<bool>(type: "boolean", nullable: false),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, defaultValue: "image/jpeg"),
                    SourceUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedAt = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TitleMedia", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TitleMedia_Files_FileId",
                        column: x => x.FileId,
                        principalSchema: "romd",
                        principalTable: "Files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TitleMedia_Titles_TitleId",
                        column: x => x.TitleId,
                        principalSchema: "romd",
                        principalTable: "Titles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TitleMetadataLayers",
                schema: "romd",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TitleId = table.Column<int>(type: "integer", nullable: false),
                    SourceId = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SourceType = table.Column<int>(type: "integer", nullable: false),
                    MetadataJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedAt = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TitleMetadataLayers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TitleMetadataLayers_Titles_TitleId",
                        column: x => x.TitleId,
                        principalSchema: "romd",
                        principalTable: "Titles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TitleSourceLinks",
                schema: "romd",
                columns: table => new
                {
                    SourceEntryId = table.Column<int>(type: "integer", nullable: false),
                    TitleId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedAt = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TitleSourceLinks", x => x.SourceEntryId);
                    table.ForeignKey(
                        name: "FK_TitleSourceLinks_SourceEntries_SourceEntryId",
                        column: x => x.SourceEntryId,
                        principalSchema: "romd",
                        principalTable: "SourceEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TitleSourceLinks_Titles_TitleId",
                        column: x => x.TitleId,
                        principalSchema: "romd",
                        principalTable: "Titles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DatGames",
                schema: "romd",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DatFileId = table.Column<int>(type: "integer", nullable: false),
                    SourceEntryId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    SearchDocument = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    SearchVector = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: false, computedColumnSql: "to_tsvector('simple', \"SearchDocument\")", stored: true),
                    Year = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    Manufacturer = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Region = table.Column<string>(type: "text", nullable: true),
                    Language = table.Column<string>(type: "text", nullable: true),
                    Revision = table.Column<string>(type: "text", nullable: true),
                    DevelopmentStatus = table.Column<string>(type: "text", nullable: true),
                    Category = table.Column<string>(type: "text", nullable: true),
                    CloneOf = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RomOf = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IsBios = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DatGames", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DatGames_DatFiles_DatFileId",
                        column: x => x.DatFileId,
                        principalSchema: "romd",
                        principalTable: "DatFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DatGames_SourceEntries_SourceEntryId",
                        column: x => x.SourceEntryId,
                        principalSchema: "romd",
                        principalTable: "SourceEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CatalogReleaseFiles",
                schema: "romd",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CatalogReleaseId = table.Column<int>(type: "integer", nullable: false),
                    FileFingerprint = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    Crc = table.Column<byte[]>(type: "bytea", maxLength: 4, nullable: true),
                    Md5 = table.Column<byte[]>(type: "bytea", maxLength: 16, nullable: true),
                    Sha1 = table.Column<byte[]>(type: "bytea", maxLength: 20, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    IsDisk = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogReleaseFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CatalogReleaseFiles_CatalogReleases_CatalogReleaseId",
                        column: x => x.CatalogReleaseId,
                        principalSchema: "romd",
                        principalTable: "CatalogReleases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CatalogReleaseLanguages",
                schema: "romd",
                columns: table => new
                {
                    CatalogReleaseId = table.Column<int>(type: "integer", nullable: false),
                    GameLanguageId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogReleaseLanguages", x => new { x.CatalogReleaseId, x.GameLanguageId });
                    table.ForeignKey(
                        name: "FK_CatalogReleaseLanguages_CatalogReleases_CatalogReleaseId",
                        column: x => x.CatalogReleaseId,
                        principalSchema: "romd",
                        principalTable: "CatalogReleases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CatalogReleaseLanguages_GameLanguages_GameLanguageId",
                        column: x => x.GameLanguageId,
                        principalSchema: "romd",
                        principalTable: "GameLanguages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CatalogReleaseRegions",
                schema: "romd",
                columns: table => new
                {
                    CatalogReleaseId = table.Column<int>(type: "integer", nullable: false),
                    RegionId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogReleaseRegions", x => new { x.CatalogReleaseId, x.RegionId });
                    table.ForeignKey(
                        name: "FK_CatalogReleaseRegions_CatalogReleases_CatalogReleaseId",
                        column: x => x.CatalogReleaseId,
                        principalSchema: "romd",
                        principalTable: "CatalogReleases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CatalogReleaseRegions_Regions_RegionId",
                        column: x => x.RegionId,
                        principalSchema: "romd",
                        principalTable: "Regions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CatalogReleaseSources",
                schema: "romd",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CatalogReleaseId = table.Column<int>(type: "integer", nullable: false),
                    SourceEntryId = table.Column<int>(type: "integer", nullable: false),
                    ProviderClaimKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AssertedTitleId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogReleaseSources", x => x.Id);
                    table.CheckConstraint("CK_CatalogReleaseSources_ProviderClaimKey_Length", "length(\"ProviderClaimKey\") BETWEEN 1 AND 200");
                    table.ForeignKey(
                        name: "FK_CatalogReleaseSources_CatalogReleases_CatalogReleaseId",
                        column: x => x.CatalogReleaseId,
                        principalSchema: "romd",
                        principalTable: "CatalogReleases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CatalogReleaseSources_SourceEntries_SourceEntryId",
                        column: x => x.SourceEntryId,
                        principalSchema: "romd",
                        principalTable: "SourceEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TrackedTitles",
                schema: "romd",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TitleId = table.Column<int>(type: "integer", nullable: false),
                    PinnedCatalogReleaseId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackedTitles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TrackedTitles_CatalogReleases_PinnedCatalogReleaseId",
                        column: x => x.PinnedCatalogReleaseId,
                        principalSchema: "romd",
                        principalTable: "CatalogReleases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_TrackedTitles_Titles_TitleId",
                        column: x => x.TitleId,
                        principalSchema: "romd",
                        principalTable: "Titles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "JobItems",
                schema: "romd",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    FileName = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Outcome = table.Column<int>(type: "integer", nullable: false),
                    RomFileId = table.Column<int>(type: "integer", nullable: true),
                    DatFileId = table.Column<int>(type: "integer", nullable: true),
                    PlatformId = table.Column<int>(type: "integer", nullable: true),
                    MatchedTitleIdsJson = table.Column<string>(type: "TEXT", nullable: false),
                    GameCount = table.Column<int>(type: "integer", nullable: true),
                    ArchiveOnly = table.Column<bool>(type: "boolean", nullable: true),
                    Error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobItems_Jobs_JobId",
                        column: x => x.JobId,
                        principalSchema: "romd",
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Collections",
                schema: "romd",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CoverMediaId = table.Column<int>(type: "integer", nullable: true),
                    PlatformId = table.Column<int>(type: "integer", nullable: true),
                    IsSystem = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedAt = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Collections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Collections_Platforms_PlatformId",
                        column: x => x.PlatformId,
                        principalSchema: "romd",
                        principalTable: "Platforms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Collections_TitleMedia_CoverMediaId",
                        column: x => x.CoverMediaId,
                        principalSchema: "romd",
                        principalTable: "TitleMedia",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "BiosGameMappings",
                schema: "romd",
                columns: table => new
                {
                    DatGameId = table.Column<int>(type: "integer", nullable: false),
                    BiosId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BiosGameMappings", x => x.DatGameId);
                    table.ForeignKey(
                        name: "FK_BiosGameMappings_Bios_BiosId",
                        column: x => x.BiosId,
                        principalSchema: "romd",
                        principalTable: "Bios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BiosGameMappings_DatGames_DatGameId",
                        column: x => x.DatGameId,
                        principalSchema: "romd",
                        principalTable: "DatGames",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DatDisks",
                schema: "romd",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DatGameId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Sha1 = table.Column<byte[]>(type: "bytea", maxLength: 20, nullable: true),
                    Md5 = table.Column<byte[]>(type: "bytea", maxLength: 16, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DatDisks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DatDisks_DatGames_DatGameId",
                        column: x => x.DatGameId,
                        principalSchema: "romd",
                        principalTable: "DatGames",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DatGameLanguages",
                schema: "romd",
                columns: table => new
                {
                    DatGameId = table.Column<int>(type: "integer", nullable: false),
                    GameLanguageId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DatGameLanguages", x => new { x.DatGameId, x.GameLanguageId });
                    table.ForeignKey(
                        name: "FK_DatGameLanguages_DatGames_DatGameId",
                        column: x => x.DatGameId,
                        principalSchema: "romd",
                        principalTable: "DatGames",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DatGameLanguages_GameLanguages_GameLanguageId",
                        column: x => x.GameLanguageId,
                        principalSchema: "romd",
                        principalTable: "GameLanguages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DatGameRegions",
                schema: "romd",
                columns: table => new
                {
                    DatGameId = table.Column<int>(type: "integer", nullable: false),
                    RegionId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DatGameRegions", x => new { x.DatGameId, x.RegionId });
                    table.ForeignKey(
                        name: "FK_DatGameRegions_DatGames_DatGameId",
                        column: x => x.DatGameId,
                        principalSchema: "romd",
                        principalTable: "DatGames",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DatGameRegions_Regions_RegionId",
                        column: x => x.RegionId,
                        principalSchema: "romd",
                        principalTable: "Regions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DatRoms",
                schema: "romd",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DatGameId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    Crc = table.Column<byte[]>(type: "bytea", maxLength: 4, nullable: true),
                    Md5 = table.Column<byte[]>(type: "bytea", maxLength: 16, nullable: true),
                    Sha1 = table.Column<byte[]>(type: "bytea", maxLength: 20, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Serial = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    RomFileId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DatRoms", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DatRoms_DatGames_DatGameId",
                        column: x => x.DatGameId,
                        principalSchema: "romd",
                        principalTable: "DatGames",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DatRoms_RomFiles_RomFileId",
                        column: x => x.RomFileId,
                        principalSchema: "romd",
                        principalTable: "RomFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "CatalogReleaseFileSources",
                schema: "romd",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CatalogReleaseFileId = table.Column<int>(type: "integer", nullable: false),
                    SourceEntryId = table.Column<int>(type: "integer", nullable: false),
                    ProviderClaimKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RequirementKind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ProviderRequirementKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogReleaseFileSources", x => x.Id);
                    table.CheckConstraint("CK_CatalogReleaseFileSources_ProviderKeys_Length", "length(\"ProviderClaimKey\") BETWEEN 1 AND 200 AND length(\"ProviderRequirementKey\") BETWEEN 1 AND 200");
                    table.CheckConstraint("CK_CatalogReleaseFileSources_RequirementKind", "\"RequirementKind\" IN ('Rom', 'Disk')");
                    table.ForeignKey(
                        name: "FK_CatalogReleaseFileSources_CatalogReleaseFiles_CatalogReleas~",
                        column: x => x.CatalogReleaseFileId,
                        principalSchema: "romd",
                        principalTable: "CatalogReleaseFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CatalogReleaseFileSources_SourceEntries_SourceEntryId",
                        column: x => x.SourceEntryId,
                        principalSchema: "romd",
                        principalTable: "SourceEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CollectionItems",
                schema: "romd",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CollectionId = table.Column<int>(type: "integer", nullable: false),
                    TitleId = table.Column<int>(type: "integer", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    AddedAt = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CollectionItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CollectionItems_Collections_CollectionId",
                        column: x => x.CollectionId,
                        principalSchema: "romd",
                        principalTable: "Collections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CollectionItems_Titles_TitleId",
                        column: x => x.TitleId,
                        principalSchema: "romd",
                        principalTable: "Titles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdminRealtimeOutboxEvents_ClaimId",
                schema: "romd",
                table: "AdminRealtimeOutboxEvents",
                column: "ClaimId");

            migrationBuilder.CreateIndex(
                name: "IX_AdminRealtimeOutboxEvents_CreatedAtUtc",
                schema: "romd",
                table: "AdminRealtimeOutboxEvents",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AdminRealtimeOutboxEvents_ProcessedAtUtc_AvailableAtUtc_Id",
                schema: "romd",
                table: "AdminRealtimeOutboxEvents",
                columns: new[] { "ProcessedAtUtc", "AvailableAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_AspNetRoleClaims_RoleId",
                schema: "romd",
                table: "AspNetRoleClaims",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                schema: "romd",
                table: "AspNetRoles",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserClaims_UserId",
                schema: "romd",
                table: "AspNetUserClaims",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserLogins_UserId",
                schema: "romd",
                table: "AspNetUserLogins",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserRoles_RoleId",
                schema: "romd",
                table: "AspNetUserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                schema: "romd",
                table: "AspNetUsers",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_LibraryId",
                schema: "romd",
                table: "AspNetUsers",
                column: "LibraryId");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                schema: "romd",
                table: "AspNetUsers",
                column: "NormalizedUserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Bios_PlatformId_NormalizedName",
                schema: "romd",
                table: "Bios",
                columns: new[] { "PlatformId", "NormalizedName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BiosGameMappings_BiosId",
                schema: "romd",
                table: "BiosGameMappings",
                column: "BiosId");

            migrationBuilder.CreateIndex(
                name: "IX_CatalogReleaseFiles_CatalogReleaseId_FileFingerprint",
                schema: "romd",
                table: "CatalogReleaseFiles",
                columns: new[] { "CatalogReleaseId", "FileFingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CatalogReleaseFiles_Sha1",
                schema: "romd",
                table: "CatalogReleaseFiles",
                column: "Sha1");

            migrationBuilder.CreateIndex(
                name: "IX_CatalogReleaseFileSources_CatalogReleaseFileId",
                schema: "romd",
                table: "CatalogReleaseFileSources",
                column: "CatalogReleaseFileId");

            migrationBuilder.CreateIndex(
                name: "UX_CatalogReleaseFileSources_ProviderRequirement",
                schema: "romd",
                table: "CatalogReleaseFileSources",
                columns: new[] { "SourceEntryId", "ProviderClaimKey", "RequirementKind", "ProviderRequirementKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CatalogReleaseLanguages_GameLanguageId",
                schema: "romd",
                table: "CatalogReleaseLanguages",
                column: "GameLanguageId");

            migrationBuilder.CreateIndex(
                name: "IX_CatalogReleaseRegions_RegionId",
                schema: "romd",
                table: "CatalogReleaseRegions",
                column: "RegionId");

            migrationBuilder.CreateIndex(
                name: "IX_CatalogReleases_CatalogTitleId",
                schema: "romd",
                table: "CatalogReleases",
                column: "CatalogTitleId");

            migrationBuilder.CreateIndex(
                name: "IX_CatalogReleases_PlatformId_Fingerprint",
                schema: "romd",
                table: "CatalogReleases",
                columns: new[] { "PlatformId", "Fingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CatalogReleases_PlatformId_PrimarySha1",
                schema: "romd",
                table: "CatalogReleases",
                columns: new[] { "PlatformId", "PrimarySha1" });

            migrationBuilder.CreateIndex(
                name: "IX_CatalogReleaseSources_CatalogReleaseId",
                schema: "romd",
                table: "CatalogReleaseSources",
                column: "CatalogReleaseId");

            migrationBuilder.CreateIndex(
                name: "IX_CatalogReleaseSources_SourceEntryId",
                schema: "romd",
                table: "CatalogReleaseSources",
                column: "SourceEntryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CollectionItems_CollectionId_TitleId",
                schema: "romd",
                table: "CollectionItems",
                columns: new[] { "CollectionId", "TitleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CollectionItems_TitleId",
                schema: "romd",
                table: "CollectionItems",
                column: "TitleId");

            migrationBuilder.CreateIndex(
                name: "IX_Collections_CoverMediaId",
                schema: "romd",
                table: "Collections",
                column: "CoverMediaId");

            migrationBuilder.CreateIndex(
                name: "IX_Collections_Name",
                schema: "romd",
                table: "Collections",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_Collections_PlatformId",
                schema: "romd",
                table: "Collections",
                column: "PlatformId");

            migrationBuilder.CreateIndex(
                name: "IX_Collections_SortOrder",
                schema: "romd",
                table: "Collections",
                column: "SortOrder");

            migrationBuilder.CreateIndex(
                name: "IX_DatDisks_DatGameId",
                schema: "romd",
                table: "DatDisks",
                column: "DatGameId");

            migrationBuilder.CreateIndex(
                name: "IX_DatDisks_Sha1",
                schema: "romd",
                table: "DatDisks",
                column: "Sha1");

            migrationBuilder.CreateIndex(
                name: "IX_DatFiles_Active_SourceId",
                schema: "romd",
                table: "DatFiles",
                column: "DatSourceId",
                unique: true,
                filter: "\"Lifecycle\" = 'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_DatFiles_FileId",
                schema: "romd",
                table: "DatFiles",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_DatFiles_Name",
                schema: "romd",
                table: "DatFiles",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_DatFiles_Pending_SourceId",
                schema: "romd",
                table: "DatFiles",
                column: "DatSourceId",
                unique: true,
                filter: "\"Lifecycle\" = 'PendingActivation'");

            migrationBuilder.CreateIndex(
                name: "IX_DatFiles_PlatformId",
                schema: "romd",
                table: "DatFiles",
                column: "PlatformId");

            migrationBuilder.CreateIndex(
                name: "IX_DatFiles_Type",
                schema: "romd",
                table: "DatFiles",
                column: "Type");

            migrationBuilder.CreateIndex(
                name: "IX_DatGameLanguages_GameLanguageId",
                schema: "romd",
                table: "DatGameLanguages",
                column: "GameLanguageId");

            migrationBuilder.CreateIndex(
                name: "IX_DatGameRegions_RegionId",
                schema: "romd",
                table: "DatGameRegions",
                column: "RegionId");

            migrationBuilder.CreateIndex(
                name: "IX_DatGames_DatFileId_IsBios_Name_Id",
                schema: "romd",
                table: "DatGames",
                columns: new[] { "DatFileId", "IsBios", "Name", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_DatGames_DatFileId_Name_Id",
                schema: "romd",
                table: "DatGames",
                columns: new[] { "DatFileId", "Name", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_DatGames_Manufacturer",
                schema: "romd",
                table: "DatGames",
                column: "Manufacturer");

            migrationBuilder.CreateIndex(
                name: "IX_DatGames_Region",
                schema: "romd",
                table: "DatGames",
                column: "Region");

            migrationBuilder.CreateIndex(
                name: "IX_DatGames_SearchVector",
                schema: "romd",
                table: "DatGames",
                column: "SearchVector")
                .Annotation("Npgsql:IndexMethod", "GIN");

            migrationBuilder.CreateIndex(
                name: "IX_DatGames_SourceEntryId",
                schema: "romd",
                table: "DatGames",
                column: "SourceEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_DatGames_Year",
                schema: "romd",
                table: "DatGames",
                column: "Year");

            migrationBuilder.CreateIndex(
                name: "IX_DatGames_Year_Id",
                schema: "romd",
                table: "DatGames",
                columns: new[] { "Year", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_DatRoms_Crc",
                schema: "romd",
                table: "DatRoms",
                column: "Crc");

            migrationBuilder.CreateIndex(
                name: "IX_DatRoms_DatGameId_Name",
                schema: "romd",
                table: "DatRoms",
                columns: new[] { "DatGameId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_DatRoms_DatGameId_RomFileId",
                schema: "romd",
                table: "DatRoms",
                columns: new[] { "DatGameId", "RomFileId" });

            migrationBuilder.CreateIndex(
                name: "IX_DatRoms_Md5",
                schema: "romd",
                table: "DatRoms",
                column: "Md5");

            migrationBuilder.CreateIndex(
                name: "IX_DatRoms_RomFileId",
                schema: "romd",
                table: "DatRoms",
                column: "RomFileId");

            migrationBuilder.CreateIndex(
                name: "IX_DatRoms_Sha1",
                schema: "romd",
                table: "DatRoms",
                column: "Sha1");

            migrationBuilder.CreateIndex(
                name: "IX_DatSources_CatalogSourceId",
                schema: "romd",
                table: "DatSources",
                column: "CatalogSourceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Files_Sha256",
                schema: "romd",
                table: "Files",
                column: "Sha256",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GameLanguageAliases_GameLanguageId",
                schema: "romd",
                table: "GameLanguageAliases",
                column: "GameLanguageId");

            migrationBuilder.CreateIndex(
                name: "IX_GameLanguageAliases_NormalizedAlias",
                schema: "romd",
                table: "GameLanguageAliases",
                column: "NormalizedAlias",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GameLanguages_Code",
                schema: "romd",
                table: "GameLanguages",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GameLanguages_Name",
                schema: "romd",
                table: "GameLanguages",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobItems_JobId_CreatedAt_Id",
                schema: "romd",
                table: "JobItems",
                columns: new[] { "JobId", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_CreatedAt",
                schema: "romd",
                table: "Jobs",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_CreatedByUserId",
                schema: "romd",
                table: "Jobs",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_Diagnostics_BulkEnrichment",
                schema: "romd",
                table: "Jobs",
                columns: new[] { "JobType", "Phase", "HangfireJobId", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_Diagnostics_ReplaceDat",
                schema: "romd",
                table: "Jobs",
                columns: new[] { "JobType", "Phase", "UpdatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_ExecutionClaim",
                schema: "romd",
                table: "Jobs",
                columns: new[] { "JobType", "Phase", "HangfireJobId", "ExecutionLeaseExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_IsArchived",
                schema: "romd",
                table: "Jobs",
                column: "IsArchived");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_IsArchived_CreatedAt",
                schema: "romd",
                table: "Jobs",
                columns: new[] { "IsArchived", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_Phase",
                schema: "romd",
                table: "Jobs",
                column: "Phase");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_PlatformId",
                schema: "romd",
                table: "Jobs",
                column: "PlatformId");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_TitleId",
                schema: "romd",
                table: "Jobs",
                column: "TitleId");

            migrationBuilder.CreateIndex(
                name: "IX_MaterializationJobs_Active_LibraryId",
                schema: "romd",
                table: "Jobs",
                column: "LibraryId",
                unique: true,
                filter: "\"JobType\" = 'materialization' AND \"Phase\" NOT IN ('Completed', 'CompletedWithErrors', 'Failed', 'Cancelled', 'Deferred')");

            migrationBuilder.CreateIndex(
                name: "IX_Libraries_ConfigurationState",
                schema: "romd",
                table: "Libraries",
                column: "ConfigurationState");

            migrationBuilder.CreateIndex(
                name: "IX_Libraries_IsDefault_Unique",
                schema: "romd",
                table: "Libraries",
                column: "IsDefault",
                unique: true,
                filter: "\"IsDefault\"");

            migrationBuilder.CreateIndex(
                name: "IX_Libraries_Name",
                schema: "romd",
                table: "Libraries",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Libraries_NeedsMaterialization",
                schema: "romd",
                table: "Libraries",
                column: "NeedsMaterialization");

            migrationBuilder.CreateIndex(
                name: "IX_MaterializedLibraryReleases_TitleId",
                schema: "romd",
                table: "MaterializedLibraryReleases",
                column: "TitleId");

            migrationBuilder.CreateIndex(
                name: "IX_MLR_LibraryId_CatalogReleaseId",
                schema: "romd",
                table: "MaterializedLibraryReleases",
                columns: new[] { "LibraryId", "CatalogReleaseId" });

            migrationBuilder.CreateIndex(
                name: "IX_MLR_LibraryId_DatGameId",
                schema: "romd",
                table: "MaterializedLibraryReleases",
                columns: new[] { "LibraryId", "DatGameId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MLR_LibraryId_IsOwned_IsExposed_TitleId",
                schema: "romd",
                table: "MaterializedLibraryReleases",
                columns: new[] { "LibraryId", "IsOwned", "IsExposed", "TitleId" });

            migrationBuilder.CreateIndex(
                name: "IX_MLR_LibraryId_IsPlayable",
                schema: "romd",
                table: "MaterializedLibraryReleases",
                columns: new[] { "LibraryId", "IsPlayable" });

            migrationBuilder.CreateIndex(
                name: "IX_MLR_LibraryId_PlatformId",
                schema: "romd",
                table: "MaterializedLibraryReleases",
                columns: new[] { "LibraryId", "PlatformId" });

            migrationBuilder.CreateIndex(
                name: "IX_MLR_LibraryId_TitleId",
                schema: "romd",
                table: "MaterializedLibraryReleases",
                columns: new[] { "LibraryId", "TitleId" });

            migrationBuilder.CreateIndex(
                name: "IX_MLR_LibraryId_TitleId_IsExposed",
                schema: "romd",
                table: "MaterializedLibraryReleases",
                columns: new[] { "LibraryId", "TitleId", "IsExposed" });

            migrationBuilder.CreateIndex(
                name: "IX_MaterializedLibraryTitles_TitleId",
                schema: "romd",
                table: "MaterializedLibraryTitles",
                column: "TitleId");

            migrationBuilder.CreateIndex(
                name: "IX_MLT_LibraryId_Genre",
                schema: "romd",
                table: "MaterializedLibraryTitles",
                columns: new[] { "LibraryId", "Genre" });

            migrationBuilder.CreateIndex(
                name: "IX_MLT_LibraryId_IsOwned_PlatformId",
                schema: "romd",
                table: "MaterializedLibraryTitles",
                columns: new[] { "LibraryId", "IsOwned", "PlatformId" });

            migrationBuilder.CreateIndex(
                name: "IX_MLT_LibraryId_IsPlayable",
                schema: "romd",
                table: "MaterializedLibraryTitles",
                columns: new[] { "LibraryId", "IsPlayable" });

            migrationBuilder.CreateIndex(
                name: "IX_MLT_LibraryId_IsVisible",
                schema: "romd",
                table: "MaterializedLibraryTitles",
                columns: new[] { "LibraryId", "IsVisible" });

            migrationBuilder.CreateIndex(
                name: "IX_MLT_LibraryId_PlatformId",
                schema: "romd",
                table: "MaterializedLibraryTitles",
                columns: new[] { "LibraryId", "PlatformId" });

            migrationBuilder.CreateIndex(
                name: "IX_MLT_LibraryId_TitleId",
                schema: "romd",
                table: "MaterializedLibraryTitles",
                columns: new[] { "LibraryId", "TitleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OpenIddictApplications_ClientId",
                schema: "romd",
                table: "OpenIddictApplications",
                column: "ClientId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OpenIddictAuthorizations_ApplicationId_Status_Subject_Type",
                schema: "romd",
                table: "OpenIddictAuthorizations",
                columns: new[] { "ApplicationId", "Status", "Subject", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_OpenIddictScopes_Name",
                schema: "romd",
                table: "OpenIddictScopes",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OpenIddictTokens_ApplicationId_Status_Subject_Type",
                schema: "romd",
                table: "OpenIddictTokens",
                columns: new[] { "ApplicationId", "Status", "Subject", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_OpenIddictTokens_AuthorizationId",
                schema: "romd",
                table: "OpenIddictTokens",
                column: "AuthorizationId");

            migrationBuilder.CreateIndex(
                name: "IX_OpenIddictTokens_ReferenceId",
                schema: "romd",
                table: "OpenIddictTokens",
                column: "ReferenceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlatformAliases_NormalizedValue_Name",
                schema: "romd",
                table: "PlatformAliases",
                column: "NormalizedValue",
                unique: true,
                filter: "\"Type\" = 0");

            migrationBuilder.CreateIndex(
                name: "IX_PlatformAliases_PlatformId",
                schema: "romd",
                table: "PlatformAliases",
                column: "PlatformId");

            migrationBuilder.CreateIndex(
                name: "IX_PlatformAliases_PlatformId_Provider",
                schema: "romd",
                table: "PlatformAliases",
                columns: new[] { "PlatformId", "Provider" },
                unique: true,
                filter: "\"Type\" = 1");

            migrationBuilder.CreateIndex(
                name: "IX_PlatformFieldDefaults_PlatformId_FieldName",
                schema: "romd",
                table: "PlatformFieldDefaults",
                columns: new[] { "PlatformId", "FieldName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Platforms_Name",
                schema: "romd",
                table: "Platforms",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_Platforms_ShortName",
                schema: "romd",
                table: "Platforms",
                column: "ShortName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RegionAliases_NormalizedAlias",
                schema: "romd",
                table: "RegionAliases",
                column: "NormalizedAlias",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RegionAliases_RegionId",
                schema: "romd",
                table: "RegionAliases",
                column: "RegionId");

            migrationBuilder.CreateIndex(
                name: "IX_Regions_Name",
                schema: "romd",
                table: "Regions",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RomFiles_Crc32",
                schema: "romd",
                table: "RomFiles",
                column: "Crc32");

            migrationBuilder.CreateIndex(
                name: "IX_RomFiles_FileId",
                schema: "romd",
                table: "RomFiles",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_RomFiles_Md5",
                schema: "romd",
                table: "RomFiles",
                column: "Md5");

            migrationBuilder.CreateIndex(
                name: "IX_RomFiles_Sha1",
                schema: "romd",
                table: "RomFiles",
                column: "Sha1",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceEntries_CatalogSourceId_EntryKey",
                schema: "romd",
                table: "SourceEntries",
                columns: new[] { "CatalogSourceId", "EntryKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceEntries_PlatformId",
                schema: "romd",
                table: "SourceEntries",
                column: "PlatformId");

            migrationBuilder.CreateIndex(
                name: "IX_TitleContentRatings_TitleId_Board",
                schema: "romd",
                table: "TitleContentRatings",
                columns: new[] { "TitleId", "Board" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TitleExternalIds_TitleId",
                schema: "romd",
                table: "TitleExternalIds",
                column: "TitleId");

            migrationBuilder.CreateIndex(
                name: "IX_TitleExternalIds_TitleId_Provider",
                schema: "romd",
                table: "TitleExternalIds",
                columns: new[] { "TitleId", "Provider" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TitleMedia_FileId",
                schema: "romd",
                table: "TitleMedia",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_TitleMedia_TitleId",
                schema: "romd",
                table: "TitleMedia",
                column: "TitleId");

            migrationBuilder.CreateIndex(
                name: "IX_TitleMedia_TitleId_Type_IsPrimary",
                schema: "romd",
                table: "TitleMedia",
                columns: new[] { "TitleId", "Type", "IsPrimary" });

            migrationBuilder.CreateIndex(
                name: "IX_TitleMedia_TitleId_Type_SourceId",
                schema: "romd",
                table: "TitleMedia",
                columns: new[] { "TitleId", "Type", "SourceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TitleMetadataLayers_TitleId_SourceId",
                schema: "romd",
                table: "TitleMetadataLayers",
                columns: new[] { "TitleId", "SourceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Titles_ConservativeMinimumAge",
                schema: "romd",
                table: "Titles",
                column: "ConservativeMinimumAge");

            migrationBuilder.CreateIndex(
                name: "IX_Titles_EnrichmentStatus",
                schema: "romd",
                table: "Titles",
                column: "EnrichmentStatus");

            migrationBuilder.CreateIndex(
                name: "IX_Titles_Genre",
                schema: "romd",
                table: "Titles",
                column: "Genre");

            migrationBuilder.CreateIndex(
                name: "IX_Titles_Name_Id",
                schema: "romd",
                table: "Titles",
                columns: new[] { "Name", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Titles_PlatformId_CatalogState",
                schema: "romd",
                table: "Titles",
                columns: new[] { "PlatformId", "CatalogState" });

            migrationBuilder.CreateIndex(
                name: "IX_Titles_PlatformId_HasLocalPayload",
                schema: "romd",
                table: "Titles",
                columns: new[] { "PlatformId", "HasLocalPayload" });

            migrationBuilder.CreateIndex(
                name: "IX_Titles_PlatformId_NormalizedName",
                schema: "romd",
                table: "Titles",
                columns: new[] { "PlatformId", "NormalizedName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Titles_SearchVector",
                schema: "romd",
                table: "Titles",
                column: "SearchVector")
                .Annotation("Npgsql:IndexMethod", "GIN");

            migrationBuilder.CreateIndex(
                name: "IX_TitleSourceLinks_TitleId",
                schema: "romd",
                table: "TitleSourceLinks",
                column: "TitleId");

            migrationBuilder.CreateIndex(
                name: "IX_TrackedTitles_PinnedCatalogReleaseId",
                schema: "romd",
                table: "TrackedTitles",
                column: "PinnedCatalogReleaseId");

            migrationBuilder.CreateIndex(
                name: "IX_TrackedTitles_TitleId",
                schema: "romd",
                table: "TrackedTitles",
                column: "TitleId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AdminRealtimeOutboxEvents",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "AspNetRoleClaims",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "AspNetUserClaims",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "AspNetUserLogins",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "AspNetUserRoles",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "AspNetUserTokens",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "BiosGameMappings",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "CatalogReleaseFileSources",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "CatalogReleaseLanguages",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "CatalogReleaseRegions",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "CatalogReleaseSources",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "CollectionItems",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "ConsumerUserSettings",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "DatDisks",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "DatGameLanguages",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "DatGameRegions",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "DatRoms",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "GameLanguageAliases",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "JobItems",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "MaterializedLibraryReleases",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "MaterializedLibraryTitles",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "OpenIddictScopes",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "OpenIddictTokens",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "PlatformAliases",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "PlatformFieldDefaults",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "RegionAliases",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "TitleContentRatings",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "TitleExternalIds",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "TitleMetadataLayers",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "TitleSourceLinks",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "TrackedTitles",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "AspNetRoles",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "Bios",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "CatalogReleaseFiles",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "Collections",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "DatGames",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "RomFiles",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "GameLanguages",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "Jobs",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "OpenIddictAuthorizations",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "Regions",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "CatalogReleases",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "TitleMedia",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "DatFiles",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "SourceEntries",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "AspNetUsers",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "OpenIddictApplications",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "Titles",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "DatSources",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "Files",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "Libraries",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "Platforms",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "CatalogSources",
                schema: "romd");
        }
    }
}
