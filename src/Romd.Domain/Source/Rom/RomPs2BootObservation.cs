namespace Romd.Domain.Source.Rom;

/// <summary>Boot layout paired with the exact completed original-byte measurement; never DVD media proof.</summary>
public sealed record RomPs2BootObservation
{
    public const string MeasurementProfile = "ps2-iso-supervised/v1";

    public RomPs2BootObservation(RomVerification measurement, RomPs2BootLayoutFacts boot)
    {
        ArgumentNullException.ThrowIfNull(measurement);
        ArgumentNullException.ThrowIfNull(boot);
        if (measurement.Encoding != RomRepresentationEncoding.Identity || measurement.Profile != MeasurementProfile ||
            measurement.Crc32.IsEmpty || boot.LogicalBytes != measurement.ContentSize ||
            boot.InspectorBuild != measurement.DecoderBuild)
            throw new ArgumentException("PS2 boot observation must bind the complete supervised original measurement and inspector build.");
        Measurement = measurement;
        Boot = boot;
    }

    public RomVerification Measurement { get; }
    public RomPs2BootLayoutFacts Boot { get; }

    /// <summary>Record equality includes the observation ID, complete hashes, lengths, builds and measured time.</summary>
    public bool Matches(RomVerification measurement) => Measurement == measurement;
}
