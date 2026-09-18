using Romd.Application.Common.ReferenceCatalog;
using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Catalog;
using Romd.Application.Common.Cqrs;
using Romd.Admin.Application.Titles.ReadModels;
using Romd.Contracts.Management.Models;

namespace Romd.Admin.Application.Titles.Commands.SetPrimaryMedia;

/// <summary>
///     Command to manually set a specific media as the primary for its type.
///     This overrides the automatic priority-based selection.
/// </summary>
public sealed record SetPrimaryMediaCommand(
    int TitleId,
    int MediaId
) : ICommand<TitleDetail>;

/// <summary>
///     Handler for <see cref="SetPrimaryMediaCommand"/>.
///     Manually sets a specific media as primary.
/// </summary>
public sealed class SetPrimaryMediaCommandHandler : ICommandHandler<SetPrimaryMediaCommand, TitleDetail>
{
    private readonly IReferenceCatalogService _referenceCatalog;
    private readonly ITitleRepository _titleRepository;
    private readonly ILogger<SetPrimaryMediaCommandHandler> _logger;

    public SetPrimaryMediaCommandHandler(IReferenceCatalogService referenceCatalog,
        ITitleRepository titleRepository,
        ILogger<SetPrimaryMediaCommandHandler> logger)
    {
        _referenceCatalog = referenceCatalog;
        _titleRepository = titleRepository;
        _logger = logger;
    }

    public async Task<ErrorOr<TitleDetail>> HandleAsync(SetPrimaryMediaCommand command, CancellationToken ct = default)
    {
        var systemKeys = await _referenceCatalog.GetSystemKeysAsync(ct);
        // 1. Load title with media collection
        var title = await _titleRepository.GetWithCollectionsAsync(command.TitleId, ct);
        if (title is null)
        {
            return CatalogErrors.TitleNotFound(command.TitleId);
        }

        // 2. Find the media by ID
        var media = title.GetMediaById(command.MediaId);
        if (media is null)
        {
            return CatalogErrors.MediaNotFound(command.MediaId);
        }

        // 3. Set as primary (clears other primaries of same type)
        title.SetPrimaryMedia(command.MediaId);

        // 4. Save changes
        await _titleRepository.UpdateAsync(title, ct);

        _logger.LogInformation(
            "Set {MediaType} media (ID: {MediaId}) as primary for title '{TitleName}' (ID: {TitleId})",
            media.Type, command.MediaId, title.Name, title.Id);

        // 5. Fetch full detail data for response
        var detailData = await _titleRepository.GetTitleDetailAsync(command.TitleId, ct);
        if (detailData is null)
        {
            return CatalogErrors.TitleNotFound(command.TitleId);
        }

        return TitleDetailMapper.ToContract(detailData, systemKeys);
    }
}
