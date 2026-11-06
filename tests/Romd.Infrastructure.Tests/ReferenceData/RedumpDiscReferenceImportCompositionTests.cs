using System.Net;
using System.Security.Authentication;
using Romd.Infrastructure.ReferenceData;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.ReferenceData;

/// <remarks>Construction checks only; these tests never send HTTPS or establish source authentication.</remarks>
public sealed class RedumpDiscReferenceImportCompositionTests
{
    [Fact]
    public void CreateTransport_ProductionRoutingAndTlsSettings_CannotRedirectDecodeOrBypassCertificates()
    {
        using var transport = RedumpDiscReferenceImportComposition.CreateTransport();
        transport.AllowAutoRedirect.ShouldBeFalse();
        transport.AutomaticDecompression.ShouldBe(DecompressionMethods.None);
        transport.MaxResponseHeadersLength.ShouldBe(16);
        transport.SslOptions.RemoteCertificateValidationCallback.ShouldBeNull();
        transport.SslOptions.LocalCertificateSelectionCallback.ShouldBeNull();
        transport.SslOptions.EnabledSslProtocols.ShouldBe(SslProtocols.None);
    }

    [Fact]
    public void CreateClient_NoCompetingTimeout_PreservesFetcherDeadline()
    {
        using var client = RedumpDiscReferenceImportComposition.CreateClient();
        client.Timeout.ShouldBe(Timeout.InfiniteTimeSpan);
    }

    [Fact]
    public void PublicComposition_NoHandlerInjection_PreservesClosedProductionConstruction()
    {
        typeof(RedumpDiscReferenceImporter).GetConstructors().ShouldBeEmpty();
        var method = typeof(RedumpDiscReferenceImportComposition).GetMethod("Create").ShouldNotBeNull();
        method.GetParameters().Select(parameter => parameter.ParameterType).ShouldBe(new[]
        {
            typeof(Romd.Admin.Application.Source.Rom.IRomDiscMediaReferenceImportStore), typeof(TimeProvider)
        });
    }
}
