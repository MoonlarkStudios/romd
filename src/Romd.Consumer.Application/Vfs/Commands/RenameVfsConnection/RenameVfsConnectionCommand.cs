using ErrorOr;
using Romd.Domain.Vfs;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;
using Romd.Consumer.Application.Vfs;
using Romd.Contracts.Consumer.Account;

namespace Romd.Consumer.Application.Vfs.Commands.RenameVfsConnection;

public sealed record RenameVfsConnectionCommand(Guid Id, string? Name, VfsFormat? Format = null) : ICommand<VfsConnectionDto>;
