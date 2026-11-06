namespace Romd.Admin.Application.Titles.Queries.OpenTitleMedia;

/// <summary>
///     Opened content of one legacy title media row. The caller owns <see cref="Content" /> and must
///     dispose it, either directly or by handing it to a result that disposes it after writing.
/// </summary>
public sealed record TitleMediaContent(Stream Content, string ContentType);
