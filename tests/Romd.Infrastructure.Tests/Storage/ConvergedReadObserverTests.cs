using System.Reflection;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Media.Probe;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class ConvergedReadObserverTests
{
    [Theory]
    [InlineData(false, 0L)]
    [InlineData(true, 1L)]
    public void OpenSynchronousRaw_CountsOpenAndMarkedMetadata(bool metadata, long expectedMetadataOpens)
    {
        var key = StorageKey.FromHash(Sha256.FromSpan(SHA256.HashData("sync observer"u8)));
        var inner = Substitute.For<IContentAddressableStore>();
        var content = Substitute.For<ISynchronousRawContent>();
        inner.OpenSynchronousRaw(key).Returns(content);
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        if (metadata)
            accessor.HttpContext.Request.Headers["X-Romd-Probe-Metadata"] = "1";

        const BindingFlags members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        Type stateType = typeof(ConvergedReadChecks).GetNestedType("ReadState", BindingFlags.NonPublic)!;
        object state = Activator.CreateInstance(stateType, members, null,
            [TimeSpan.Zero, Array.Empty<(RomContentDescriptor Content, string IsoPath, string SystemKey)>(), ConvergedReadProfile.NintendoWholeFiles], null)!;
        Type observerType = typeof(ConvergedReadChecks).GetNestedType("ForwardingStore", BindingFlags.NonPublic)!;
        var observer = (IContentAddressableStore)Activator.CreateInstance(observerType, members, null,
            [inner, state, accessor], null)!;

        try
        {
            observer.OpenSynchronousRaw(key).ShouldBeSameAs(content);
            inner.Received(1).OpenSynchronousRaw(key);
            ((long)stateType.GetField("CasOpens", members)!.GetValue(state)!).ShouldBe(1);
            ((long)stateType.GetField("MetadataCasOpens", members)!.GetValue(state)!).ShouldBe(expectedMetadataOpens);
        }
        finally
        {
            accessor.HttpContext = null;
        }
    }
}
