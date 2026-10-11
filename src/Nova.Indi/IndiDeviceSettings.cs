using Nova.Core.Equipment;

namespace Nova.Indi;

/// <summary>Exposes every INDI property of every device, for the generic driver settings panel.</summary>
public sealed class IndiDeviceSettings(IndiClient client) : IDeviceSettings
{
    public IReadOnlyList<DeviceProperty> GetProperties(string device) =>
        client.GetProperties(device).Select(ToModel).ToList();

    public async Task SetPropertyAsync(string device, string property, IReadOnlyDictionary<string, string> values, CancellationToken ct)
    {
        var p = client.GetProperty(device, property)
            ?? throw new ArgumentException($"{device} has no property {property}");
        if (p.Permission == "ro") throw new ArgumentException($"{p.Label} is read-only");
        if (values.Count == 0) throw new ArgumentException("No values given");
        foreach (var name in values.Keys)
            if (!p.Elements.ContainsKey(name)) throw new ArgumentException($"{p.Label} has no element {name}");

        switch (p.Type)
        {
            case IndiPropertyType.Number:
                // Send the whole vector: some drivers (e.g. coordinates) expect every element together.
                var numbers = new List<(string, double)>();
                foreach (var element in p.Elements.Values)
                {
                    if (values.TryGetValue(element.Name, out var text))
                    {
                        if (!IndiNumber.TryParse(text, out var number))
                            throw new ArgumentException($"'{text}' is not a number");
                        numbers.Add((element.Name, number));
                    }
                    else if (element.TryNumber is { } current)
                    {
                        numbers.Add((element.Name, current));
                    }
                }
                await client.SetNumbersAsync(device, property, ct, [.. numbers]);
                break;

            case IndiPropertyType.Switch:
                // Only the switches being changed; the driver applies the rule (e.g. one-of-many).
                var switches = values.Select(v => (v.Key, ParseSwitch(v.Value))).ToArray();
                await client.SetSwitchesAsync(device, property, ct, switches);
                break;

            case IndiPropertyType.Text:
                var texts = p.Elements.Values
                    .Select(e => (e.Name, values.TryGetValue(e.Name, out var text) ? text : e.Text))
                    .ToArray();
                await client.SetTextsAsync(device, property, ct, texts);
                break;

            default:
                throw new ArgumentException($"{p.Type} properties can't be set");
        }
    }

    private static bool ParseSwitch(string value) => value.Trim().ToLowerInvariant() switch
    {
        "on" or "true" or "1" => true,
        "off" or "false" or "0" => false,
        _ => throw new ArgumentException($"'{value}' is not On or Off"),
    };

    private static DeviceProperty ToModel(IndiProperty p)
    {
        var kind = p.Type switch
        {
            IndiPropertyType.Number => DevicePropertyKind.Number,
            IndiPropertyType.Switch => DevicePropertyKind.Switch,
            IndiPropertyType.Text => DevicePropertyKind.Text,
            IndiPropertyType.Light => DevicePropertyKind.Light,
            _ => DevicePropertyKind.Blob,
        };

        var elements = p.Elements.Values.Select(e => p.Type switch
        {
            IndiPropertyType.Number => new DevicePropertyElement(e.Name, e.Label, e.Text,
                e.TryNumber is { } n ? IndiNumber.Format(n, e.Format) : e.Text, e.Min, e.Max, e.Step),
            // Never ship image data through the settings API.
            IndiPropertyType.Blob => new DevicePropertyElement(e.Name, e.Label, "", e.BlobFormat ?? ""),
            _ => new DevicePropertyElement(e.Name, e.Label, e.Text, e.Text),
        }).ToList();

        return new DeviceProperty(
            p.Name,
            string.IsNullOrEmpty(p.Label) ? p.Name : p.Label,
            string.IsNullOrEmpty(p.Group) ? "General" : p.Group,
            kind,
            p.Permission,
            p.State.ToString(),
            p.Type == IndiPropertyType.Switch ? p.Rule.ToString() : null,
            elements);
    }
}
