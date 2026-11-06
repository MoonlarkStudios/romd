namespace Romd.Persistence.MediaFacts;

/// <summary>Guarded fact-model DDL installed by the one supported fresh application baseline.</summary>
public static class MediaFactSchema
{
    public static string CreateSql => Read("Schema") + "\n" + Read("ContainerFacts") + "\n" + Read("InspectionFacts") + "\n" + Read("CreationFacts") + "\n" + Read("CdObservationFacts");

    public static string OwnershipSql => Read("OwnershipFacts") + "\n" + Read("CatalogCdLayoutFacts");
    public static string SourcePinSql => Read("SourcePinFacts");

    public static string ReconciliationSql => Read("ReconciliationSchema") + "\n" + Read("ReconciliationGuards");

    public static string BaselineSupportSql => Read("BaselineSupport");
    public static string RuntimeGrantsSql => Read("RuntimeFactGrants");

    private static string Read(string name)
    {
        using var stream = typeof(MediaFactSchema).Assembly.GetManifestResourceStream($"Romd.MediaFacts.{name}.sql")
            ?? throw new InvalidOperationException("The media fact schema resource is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
