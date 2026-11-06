using System.Reflection;
using Romd.Domain.Source.Dat;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Source.Dat;

public sealed class DatGameEncapsulationTests
{
    [Fact]
    public void ChildCollections_MutableInterfaces_RejectChangesAndKeepLiveViews()
    {
        var game = DatGame.CreateNew(1, "game");
        var roms = game.Roms;
        var disks = game.Disks;
        var rom = DatRom.CreateNew("game.rom", 128);
        var disk = DatDisk.CreateNew("game.chd");

        game.AddRom(rom);
        game.AddDisk(disk);
        Should.Throw<NotSupportedException>(() => ((ICollection<DatRom>)roms).Clear());
        Should.Throw<NotSupportedException>(() => ((ICollection<DatDisk>)disks).Clear());
        Should.Throw<NotSupportedException>(() => ((IList<DatRom>)roms)[0] = DatRom.CreateNew("replacement", 1));
        Should.Throw<NotSupportedException>(() => ((IList<DatDisk>)disks)[0] = DatDisk.CreateNew("replacement"));

        game.Roms.ShouldBeSameAs(roms);
        game.Disks.ShouldBeSameAs(disks);
        roms.ShouldHaveSingleItem().ShouldBeSameAs(rom);
        disks.ShouldHaveSingleItem().ShouldBeSameAs(disk);
    }

    [Fact]
    public void Rehydrate_MutableChildInputs_RetainsOwnedCollections()
    {
        var rom = DatRom.CreateNew("game.rom", 128);
        var disk = DatDisk.CreateNew("game.chd");
        var roms = new List<DatRom> { rom };
        var disks = new List<DatDisk> { disk };
        var game = DatGame.Rehydrate(7, 1, "game", null, null, null, null, null, null, null,
            null, null, null, false, roms, disks);

        roms.Clear();
        disks.Clear();

        game.Roms.ShouldHaveSingleItem().ShouldBeSameAs(rom);
        game.Disks.ShouldHaveSingleItem().ShouldBeSameAs(disk);
        Should.Throw<NotSupportedException>(() => ((ICollection<DatRom>)game.Roms).Clear());
        Should.Throw<NotSupportedException>(() => ((ICollection<DatDisk>)game.Disks).Clear());
        game.AddRom(DatRom.CreateNew("second.rom", 256));
        game.AddDisk(DatDisk.CreateNew("second.chd"));
        roms.ShouldBeEmpty();
        disks.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(typeof(DatRom))]
    [InlineData(typeof(DatDisk))]
    public void RetainedElements_PublicSurface_DoesNotPermitInPlaceMutation(Type elementType)
    {
        elementType.GetProperties().Where(property => property.SetMethod?.IsPublic == true).ShouldBeEmpty();
        elementType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName && method.ReturnType == typeof(void)).ShouldBeEmpty();
    }
}
