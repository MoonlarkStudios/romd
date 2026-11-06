using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;
using Romd.Consumer.Application.Vfs;
using Romd.Contracts.Consumer.Account;

namespace Romd.Consumer.Application.Vfs.Commands.RotateVfsConnection;

public sealed record RotateVfsConnectionCommand(Guid Id) : ICommand<IssuedVfsCredential>;
