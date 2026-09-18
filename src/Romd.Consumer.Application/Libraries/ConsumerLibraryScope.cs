namespace Romd.Consumer.Application.Libraries;

public sealed record ConsumerLibraryScope(Guid UserId);

public abstract record ConsumerLibraryReadResult<T>
    where T : notnull
{
    private ConsumerLibraryReadResult()
    {
    }

    private protected abstract void SealConcreteVariants();

    public sealed record Found(int LibraryId, T Value) : ConsumerLibraryReadResult<T>
    {
        private protected override void SealConcreteVariants()
        {
        }
    }

    public sealed record ItemNotFound(int LibraryId) : ConsumerLibraryReadResult<T>
    {
        private protected override void SealConcreteVariants()
        {
        }
    }

    public sealed record LibraryUnavailable : ConsumerLibraryReadResult<T>
    {
        private protected override void SealConcreteVariants()
        {
        }
    }

    public sealed record ProjectionInconsistent : ConsumerLibraryReadResult<T>
    {
        private protected override void SealConcreteVariants()
        {
        }
    }
}

public abstract record ConsumerLibraryProjectionResult<T>
    where T : notnull
{
    private ConsumerLibraryProjectionResult()
    {
    }

    private protected abstract void SealConcreteVariants();

    public sealed record Found(T Value) : ConsumerLibraryProjectionResult<T>
    {
        private protected override void SealConcreteVariants()
        {
        }
    }

    public sealed record ItemNotFound : ConsumerLibraryProjectionResult<T>
    {
        private protected override void SealConcreteVariants()
        {
        }
    }

    public sealed record ProjectionInconsistent : ConsumerLibraryProjectionResult<T>
    {
        private protected override void SealConcreteVariants()
        {
        }
    }
}
