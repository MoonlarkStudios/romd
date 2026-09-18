using Romd.Admin.Application.Libraries;
using Contract = Romd.Contracts.Management.Libraries;
using ContractModels = Romd.Contracts.Management.Models;
using DomainRatings = Romd.Domain.Catalog.Ratings;
using DomainLibraryTitleSelectionMode = Romd.Domain.Libraries.LibraryTitleSelectionMode;
using DomainRatingBasisSelection = Romd.Domain.Libraries.RatingBasisSelection;
using DomainUnknownMetadataPolicy = Romd.Domain.Libraries.UnknownMetadataPolicy;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Libraries;

public sealed class LibraryConfigurationContractMappingTests
{
    [Fact]
    public void PolicyEnumMappings_AllDefinedValuesRoundTrip()
    {
        AssertRoundTrip<DomainLibraryTitleSelectionMode, Contract.LibraryTitleSelectionMode>(
            LibraryConfigurationContractMapping.ToContract,
            LibraryConfigurationContractMapping.ToDomain);
        AssertRoundTrip<DomainRatingBasisSelection, Contract.RatingBasisSelection>(
            LibraryConfigurationContractMapping.ToContract,
            LibraryConfigurationContractMapping.ToDomain);
        AssertRoundTrip<DomainUnknownMetadataPolicy, Contract.UnknownMetadataPolicy>(
            LibraryConfigurationContractMapping.ToContract,
            LibraryConfigurationContractMapping.ToDomain);
        AssertRoundTrip<DomainRatings.RatingBoard, ContractModels.RatingBoard>(
            LibraryConfigurationContractMapping.ToContract,
            LibraryConfigurationContractMapping.ToDomain);
    }

    [Fact]
    public void PolicyEnumMappings_UnmirroredValuesThrow()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            LibraryConfigurationContractMapping.ToContract((DomainLibraryTitleSelectionMode)int.MaxValue));
        Should.Throw<ArgumentOutOfRangeException>(() =>
            LibraryConfigurationContractMapping.ToDomain((Contract.LibraryTitleSelectionMode)int.MaxValue));
        Should.Throw<ArgumentOutOfRangeException>(() =>
            LibraryConfigurationContractMapping.ToContract((DomainRatingBasisSelection)int.MaxValue));
        Should.Throw<ArgumentOutOfRangeException>(() =>
            LibraryConfigurationContractMapping.ToDomain((Contract.RatingBasisSelection)int.MaxValue));
        Should.Throw<ArgumentOutOfRangeException>(() =>
            LibraryConfigurationContractMapping.ToContract((DomainUnknownMetadataPolicy)int.MaxValue));
        Should.Throw<ArgumentOutOfRangeException>(() =>
            LibraryConfigurationContractMapping.ToDomain((Contract.UnknownMetadataPolicy)int.MaxValue));
        Should.Throw<ArgumentOutOfRangeException>(() =>
            LibraryConfigurationContractMapping.ToContract((DomainRatings.RatingBoard)int.MaxValue));
        Should.Throw<ArgumentOutOfRangeException>(() =>
            LibraryConfigurationContractMapping.ToDomain((ContractModels.RatingBoard)int.MaxValue));
    }

    private static void AssertRoundTrip<TDomain, TContract>(
        Func<TDomain, TContract> toContract,
        Func<TContract, TDomain> toDomain)
        where TDomain : struct, Enum
        where TContract : struct, Enum
    {
        var domainValues = Enum.GetValues<TDomain>();
        var contractValues = Enum.GetValues<TContract>();

        domainValues.Length.ShouldBe(contractValues.Length);
        foreach (TDomain domainValue in domainValues)
        {
            TContract contractValue = toContract(domainValue);
            contractValue.ToString().ShouldBe(domainValue.ToString());
            toDomain(contractValue).ShouldBe(domainValue);
        }
    }
}
