namespace Nova.Core.Equipment;

// A protocol-neutral view of "everything the driver exposes", for a generic settings panel.
// Modelled on INDI properties, which group named elements into a vector with one state.

public enum DevicePropertyKind { Number, Switch, Text, Light, Blob }

/// <param name="Value">Raw value as the driver sent it ("On"/"Off" for switches).</param>
/// <param name="Display">Value formatted the way the driver asks (e.g. sexagesimal for coordinates).</param>
public sealed record DevicePropertyElement(
    string Name, string Label, string Value, string Display,
    double? Min = null, double? Max = null, double? Step = null);

/// <param name="Permission">"ro", "wo" or "rw".</param>
/// <param name="State">"Idle", "Ok", "Busy" or "Alert".</param>
/// <param name="Rule">For switches: "OneOfMany", "AtMostOne" or "AnyOfMany".</param>
public sealed record DeviceProperty(
    string Name, string Label, string Group, DevicePropertyKind Kind,
    string Permission, string State, string? Rule,
    IReadOnlyList<DevicePropertyElement> Elements);

public interface IDeviceSettings
{
    /// <summary>All properties of a device, or an empty list if the device doesn't exist.</summary>
    IReadOnlyList<DeviceProperty> GetProperties(string device);

    /// <summary>
    /// Sends new values for some elements of a property. Numbers may be decimal or sexagesimal text;
    /// switches take "On"/"Off" (or true/false). Throws ArgumentException for unknown or read-only properties.
    /// </summary>
    Task SetPropertyAsync(string device, string property, IReadOnlyDictionary<string, string> values, CancellationToken ct);
}
