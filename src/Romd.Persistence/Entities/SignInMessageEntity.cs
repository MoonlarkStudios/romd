using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Contracts.Management.Models;

namespace Romd.Persistence.Entities;

internal sealed class SignInMessageEntity
{
    public int Id { get; init; } = 1;
    public Guid Revision { get; init; }
    public string Message { get; init; } = string.Empty;

    public static void Configure(EntityTypeBuilder<SignInMessageEntity> builder)
    {
        builder.ToTable("SignInMessage", table => table.HasCheckConstraint("CK_SignInMessage_Singleton", "\"Id\" = 1"));
        builder.HasKey(row => row.Id);
        builder.Property(row => row.Id).ValueGeneratedNever();
        builder.Property(row => row.Message).HasMaxLength(ReplaceSignInMessageRequest.MaximumLength);
        builder.HasData(new SignInMessageEntity());
    }
}
