namespace Romd.Application.Common.Security;

public interface ISignInMessageReader
{
    Task<string> GetMessageAsync(CancellationToken ct);
}
