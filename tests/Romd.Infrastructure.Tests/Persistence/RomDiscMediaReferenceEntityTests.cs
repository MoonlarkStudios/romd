using Romd.Domain.Source.Rom;
using System.Data;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Romd.Admin.Application.Source.Rom;
using Romd.Domain.Hashing;
using Romd.Persistence.Entities;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class RomDiscMediaReferenceEntityTests
{
    [Fact]
    public void FromDomain_RoundTrip_PreservesEveryPublishedAndProvenanceFact()
    {
        byte[] raw = [0, 255, 4, 3, 2, 1];
        var record = NewRecord(raw);
        var row = RomDiscMediaReferenceEntity.FromDomain(record, raw);

        row.ToDomain().ShouldBe(record);
        row.Id.ShouldBe(record.Id);
        row.Crc32.ShouldBe(record.Crc32.ToUInt32());
        row.RetrievedAtUtcTicks.ShouldBe(record.RetrievedAt.UtcTicks);
        row.GetRawBodySnapshot().ShouldBe(raw);
    }

    [Fact]
    public void FromDomain_InputAndSnapshotMutation_CannotChangeRetainedCustody()
    {
        byte[] raw = [5, 4, 3, 2, 1];
        byte[] original = raw.ToArray();
        var record = NewRecord(raw);
        var row = RomDiscMediaReferenceEntity.FromDomain(record, raw);
        raw[0] ^= 255;
        byte[] snapshot = row.GetRawBodySnapshot();
        snapshot[1] ^= 255;

        row.GetRawBodySnapshot().ShouldBe(original);
        row.ToDomain().ShouldBe(record);
        row.GetRawBodySnapshot().ShouldNotBeSameAs(snapshot);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(uint.MaxValue)]
    public void FromDomain_CompleteZeroAndMaximumCrc_ArePresentValues(uint crc)
    {
        byte[] raw = [1, 2, 3];
        var record = NewRecord(raw, crc: crc);
        var row = RomDiscMediaReferenceEntity.FromDomain(record, raw);
        row.Crc32.ShouldBe(crc);
        row.ToDomain().Crc32.ShouldBe(Crc32.FromUInt32(crc));
        row.ToDomain().Crc32.IsEmpty.ShouldBeFalse();
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(1234L)]
    public void FromDomain_RetrievalTime_RetainsExactUtcTicksRatherThanMilliseconds(long afterEpoch)
    {
        byte[] raw = [1, 2, 3];
        var observed = DateTimeOffset.UnixEpoch.AddTicks(afterEpoch).ToOffset(TimeSpan.FromHours(-5));
        var record = NewRecord(raw, retrievedAt: observed);
        var row = RomDiscMediaReferenceEntity.FromDomain(record, raw);
        row.RetrievedAtUtcTicks.ShouldBe(observed.UtcTicks);
        row.ToDomain().RetrievedAt.ShouldBe(observed.ToUniversalTime());
        row.ToDomain().RetrievedAt.Offset.ShouldBe(TimeSpan.Zero);
        row.ToDomain().RetrievedAt.ShouldNotBe(DateTimeOffset.FromUnixTimeMilliseconds(observed.ToUnixTimeMilliseconds()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public void FromDomain_AbsentShortOrLongRawBody_RejectsLengthMismatch(int length)
    {
        var record = NewRecord([1, 2, 3]);
        Should.Throw<ArgumentException>(() => RomDiscMediaReferenceEntity.FromDomain(record, new byte[length]));
    }

    [Fact]
    public void FromDomain_EqualLengthDifferentBytes_RejectsHashMismatch()
    {
        var record = NewRecord([1, 2, 3]);
        Should.Throw<InvalidOperationException>(() => RomDiscMediaReferenceEntity.FromDomain(record, [1, 2, 4]));
    }

    [Fact]
    public void FromDomain_MaximumRawBody_IsRetainedExactly()
    {
        byte[] raw = new byte[checked((int)RomDiscMediaReferenceRecord.MaximumRawReferenceBytes)];
        raw[0] = 255;
        raw[^1] = 127;
        var record = NewRecord(raw);
        var row = RomDiscMediaReferenceEntity.FromDomain(record, raw);
        row.GetRawBodySnapshot().ShouldBe(raw);
        row.ToDomain().ShouldBe(record);
    }

    [Theory]
    [InlineData(RomDiscReferenceSystem.PlayStation1, RomDiscReferenceMedia.Cd, 1)]
    [InlineData(RomDiscReferenceSystem.PlayStation2, RomDiscReferenceMedia.Dvd5, 1)]
    [InlineData(RomDiscReferenceSystem.PlayStation2, RomDiscReferenceMedia.Dvd9, 2)]
    public void FromDomain_ExplicitGenericReferenceFacts_DoesNotInventSingletonDvdEligibility(
        RomDiscReferenceSystem system, RomDiscReferenceMedia media, int count)
    {
        byte[] raw = [1, 2, 3];
        var record = NewRecord(raw, system: system, media: media, fileCount: count);
        RomDiscMediaReferenceEntity.FromDomain(record, raw).ToDomain().ShouldBe(record);
    }

    [Theory]
    [InlineData("Id")]
    [InlineData("System")]
    [InlineData("Media")]
    [InlineData("FileCount")]
    [InlineData("LogicalBytes")]
    [InlineData("Sha1")]
    [InlineData("Md5")]
    [InlineData("ReferenceSource")]
    [InlineData("ParserProfile")]
    [InlineData("AcceptedSourceProvenance")]
    [InlineData("RetrievedAtUtcTicks")]
    public void ToDomain_CorruptMaterializedScalar_RejectsRatherThanFabricatingRecord(string property)
    {
        byte[] raw = [1, 2, 3];
        var row = RomDiscMediaReferenceEntity.FromDomain(NewRecord(raw), raw);
        object invalid = property switch
        {
            "Id" => Guid.Empty,
            "System" => (RomDiscReferenceSystem)99,
            "Media" => (RomDiscReferenceMedia)99,
            "FileCount" => 0,
            "LogicalBytes" => 0L,
            "Sha1" => default(Sha1),
            "Md5" => default(Md5),
            "RetrievedAtUtcTicks" => -1L,
            "ReferenceSource" => "untrusted\nsource",
            "ParserProfile" => new string('x', 129),
            _ => " "
        };
        SetMaterializedProperty(row, property, invalid);
        Should.Throw<ArgumentException>(() => row.ToDomain());
    }

    [Theory]
    [InlineData("RawBody")]
    [InlineData("RawReferenceBytes")]
    [InlineData("RawReferenceSha256")]
    public void ToDomain_AndSnapshot_CorruptRawCustody_RejectBothAccessPaths(string property)
    {
        byte[] raw = [1, 2, 3];
        var row = RomDiscMediaReferenceEntity.FromDomain(NewRecord(raw), raw);
        object invalid = property switch
        {
            "RawBody" => new byte[] { 1, 2, 4 },
            "RawReferenceBytes" => 4L,
            _ => Sha256.Parse(new string('a', 64))
        };
        SetMaterializedProperty(row, property, invalid);
        Should.Throw<InvalidOperationException>(() => row.ToDomain());
        Should.Throw<InvalidOperationException>(() => row.GetRawBodySnapshot());
    }

    [Fact]
    public void PublicApi_HasNoMutableScalarOrRawArrayBackingAccess()
    {
        var properties = typeof(RomDiscMediaReferenceEntity).GetProperties(BindingFlags.Instance | BindingFlags.Public);
        properties.ShouldAllBe(property => property.SetMethod == null || !property.SetMethod.IsPublic);
        properties.ShouldAllBe(property => property.PropertyType != typeof(byte[]));
        typeof(RomDiscMediaReferenceEntity).GetConstructors().ShouldBeEmpty();
    }

    [Fact]
    public void Configure_Model_UsesExactTypedColumnsNonuniqueTupleAndPostSaveImmutability()
    {
        using var db = new ReferenceModelContext(new DbContextOptionsBuilder<ReferenceModelContext>()
            .UseNpgsql("Host=localhost;Database=romd;Username=romd").Options);
        var entity = db.Model.FindEntityType(typeof(RomDiscMediaReferenceEntity)).ShouldNotBeNull();
        entity.GetTableName().ShouldBe("RomDiscMediaReferences");
        entity.FindPrimaryKey().ShouldNotBeNull().Properties.ShouldHaveSingleItem().Name.ShouldBe("Id");
        entity.FindProperty("Id").ShouldNotBeNull().ValueGenerated.ShouldBe(ValueGenerated.Never);
        entity.GetForeignKeys().ShouldBeEmpty();
        entity.GetProperties().ShouldAllBe(property => property.GetAfterSaveBehavior() == PropertySaveBehavior.Throw);
        entity.GetProperties().ShouldAllBe(property => property.GetColumnType() != "jsonb");
        var index = entity.GetIndexes().ShouldHaveSingleItem();
        index.IsUnique.ShouldBeFalse();
        index.Properties.Select(property => property.Name).ShouldBe(new[] { "LogicalBytes", "Sha1", "Md5", "Crc32" });
        entity.FindProperty("Crc32").ShouldNotBeNull().GetTypeMapping().Converter.ShouldNotBeNull().ProviderClrType.ShouldBe(typeof(long));
        foreach (var name in new[] { "Sha1", "Md5", "RawReferenceSha256" })
            entity.FindProperty(name).ShouldNotBeNull().GetTypeMapping().Converter.ShouldNotBeNull().ProviderClrType.ShouldBe(typeof(byte[]));
        entity.FindProperty("RawBody").ShouldNotBeNull().GetColumnType().ShouldBe("bytea");
        entity.FindProperty("RawBody").ShouldNotBeNull().IsNullable.ShouldBeFalse();
        entity.FindProperty("RawBody").ShouldNotBeNull().IsShadowProperty().ShouldBeFalse();
        entity.FindProperty("RawBody").ShouldNotBeNull().PropertyInfo.ShouldNotBeNull().GetMethod.ShouldNotBeNull().IsPrivate.ShouldBeTrue();
        entity.FindProperty("RawBody").ShouldNotBeNull().GetMaxLength().ShouldBe(1048576);
        entity.FindProperty("RetrievedAtUtcTicks").ShouldNotBeNull().ClrType.ShouldBe(typeof(long));
        entity.FindProperty("ReferenceSource").ShouldNotBeNull().GetMaxLength().ShouldBe(256);
        entity.FindProperty("ParserProfile").ShouldNotBeNull().GetMaxLength().ShouldBe(128);
        entity.FindProperty("AcceptedSourceProvenance").ShouldNotBeNull().GetMaxLength().ShouldBe(256);
        db.Database.GetDbConnection().State.ShouldBe(ConnectionState.Closed);
    }

    private static RomDiscMediaReferenceRecord NewRecord(byte[] raw, uint crc = uint.MaxValue,
        DateTimeOffset? retrievedAt = null, RomDiscReferenceSystem system = RomDiscReferenceSystem.PlayStation2,
        RomDiscReferenceMedia media = RomDiscReferenceMedia.Dvd5, int fileCount = 1) =>
        new(new Guid("00000000-0000-0000-0000-000000000001"), system, media, fileCount, 8L << 30,
            Sha1.Parse(new string('1', 40)), Md5.Parse(new string('2', 32)), Crc32.FromUInt32(crc), raw.LongLength,
            Sha256.FromSpan(SHA256.HashData(raw)), "https://example.test/trusted/reference/1", "synthetic-reference/v1",
            "caller-admitted-local-fixture", retrievedAt ?? DateTimeOffset.UnixEpoch.AddTicks(1234));

    // Simulates corrupt EF materialization without adding a public mutation path to production.
    private static void SetMaterializedProperty(RomDiscMediaReferenceEntity row, string name, object value) =>
        typeof(RomDiscMediaReferenceEntity).GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .ShouldNotBeNull().SetValue(row, value);

    private sealed class ReferenceModelContext(DbContextOptions<ReferenceModelContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            RomDiscMediaReferenceEntity.Configure(modelBuilder.Entity<RomDiscMediaReferenceEntity>());
    }
}
