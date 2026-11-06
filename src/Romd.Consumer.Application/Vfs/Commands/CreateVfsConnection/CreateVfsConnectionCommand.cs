using ErrorOr;
using Romd.Domain.Vfs;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;
using Romd.Consumer.Application.Vfs;
using Romd.Contracts.Consumer.Account;

namespace Romd.Consumer.Application.Vfs.Commands.CreateVfsConnection;

public sealed record CreateVfsConnectionCommand(string Name, VfsFormat Format = VfsFormat.Stored) : ICommand<IssuedVfsCredential>;
