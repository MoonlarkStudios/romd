using System.Text;

namespace Romd.Storage.Tests;

/// <summary>
///     Small, synthetic content vectors with independently verifiable hashes.
///     The ASCII values are standard checksum examples and contain no ROM data.
/// </summary>
public static class TestContents
{
    public static readonly ContentTestData Abc = new(
        "ASCII abc",
        Encoding.ASCII.GetBytes("abc"),
        "352441c2",
        "900150983cd24fb0d6963f7d28e17f72",
        "a9993e364706816aba3e25717850c26c9cd0d89d",
        "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");

    public static readonly ContentTestData Digits = new(
        "ASCII 123456789",
        Encoding.ASCII.GetBytes("123456789"),
        "cbf43926",
        "25f9e794323b453885f5181f1b624d0b",
        "f7c3bc1d808e04732adf679965ccc34ca7ae3441",
        "15e2b0d3c33891ebb0f1ef609ec419420c20e320ce94c65fbc8c3312448eb225");

    public static IEnumerable<object[]> All()
    {
        yield return [Abc];
        yield return [Digits];
    }
}

public sealed record ContentTestData(
    string Name,
    byte[] Contents,
    string Crc32,
    string Md5,
    string Sha1,
    string Sha256)
{
    public int Size => Contents.Length;

    public Stream OpenStream() => new MemoryStream(Contents, writable: false);

    public override string ToString() => Name;
}
