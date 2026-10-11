using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;

namespace Nova.Indi;

// INDI wire protocol v1.7: https://www.indilib.org/develop/developer-manual/104-scripting.html
// The server streams top-level XML elements (no root element) over TCP port 7624.

public enum IndiPropertyType { Number, Switch, Text, Light, Blob }

public enum IndiState { Idle, Ok, Busy, Alert }

public enum IndiSwitchRule { OneOfMany, AtMostOne, AnyOfMany }

public sealed class IndiElement
{
    public required string Name { get; init; }
    public string Label { get; init; } = "";
    /// <summary>Raw text as sent by the server ("On"/"Off" for switches, the number for numbers).</summary>
    public string Text { get; set; } = "";

    // Number metadata (from def*). Format is printf-like, "%m" variants mean sexagesimal.
    public string? Format { get; init; }
    public double Min { get; init; }
    public double Max { get; init; }
    public double Step { get; init; }

    // BLOB payload (from set*). Already decompressed if the server sent ".z".
    public byte[]? BlobData { get; set; }
    public string? BlobFormat { get; set; }

    public double Number => IndiNumber.Parse(Text);
    public double? TryNumber => IndiNumber.TryParse(Text, out var value) ? value : null;
    public bool IsOn => Text.Trim().Equals("On", StringComparison.OrdinalIgnoreCase);
}

public sealed class IndiProperty
{
    public required string Device { get; init; }
    public required string Name { get; init; }
    public required IndiPropertyType Type { get; init; }
    public string Label { get; init; } = "";
    public string Group { get; init; } = "";
    public string Permission { get; init; } = "ro";
    public IndiSwitchRule Rule { get; init; } = IndiSwitchRule.AnyOfMany;
    public IndiState State { get; set; }
    public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;
    public Dictionary<string, IndiElement> Elements { get; } = new(StringComparer.Ordinal);

    public IndiElement? this[string element] => Elements.GetValueOrDefault(element);

    public override string ToString() => $"{Device}.{Name} [{Type}, {State}]";
}

/// <summary>A parsed message from the server.</summary>
public abstract record IndiMessage(string Device, string? Message);

public sealed record IndiDefineMessage(IndiProperty Property, string? Message) : IndiMessage(Property.Device, Message);

public sealed record IndiElementValue(string Name, string Text, byte[]? BlobData = null, string? BlobFormat = null);

public sealed record IndiSetMessage(
    string Device, string Name, IndiPropertyType Type, IndiState? State,
    IReadOnlyList<IndiElementValue> Values, string? Message) : IndiMessage(Device, Message);

public sealed record IndiDeleteMessage(string Device, string? Name, string? Message) : IndiMessage(Device, Message);

public sealed record IndiTextMessage(string Device, string? Message) : IndiMessage(Device, Message);

public static class IndiParser
{
    /// <summary>Converts one top-level element from the server into a message, or null if not understood.</summary>
    public static IndiMessage? Parse(XElement element)
    {
        var tag = element.Name.LocalName;
        var device = (string?)element.Attribute("device") ?? "";
        var message = (string?)element.Attribute("message");

        if (tag == "message") return new IndiTextMessage(device, message);
        if (tag == "delProperty") return new IndiDeleteMessage(device, (string?)element.Attribute("name"), message);

        if (tag.StartsWith("def", StringComparison.Ordinal) && tag.EndsWith("Vector", StringComparison.Ordinal))
            return ParseDefine(element, tag[3..^6], device, message);

        if (tag.StartsWith("set", StringComparison.Ordinal) && tag.EndsWith("Vector", StringComparison.Ordinal))
            return ParseSet(element, tag[3..^6], device, message);

        return null;
    }

    private static IndiDefineMessage? ParseDefine(XElement element, string typeName, string device, string? message)
    {
        if (!TryParseType(typeName, out var type)) return null;

        var property = new IndiProperty
        {
            Device = device,
            Name = (string?)element.Attribute("name") ?? "",
            Type = type,
            Label = (string?)element.Attribute("label") ?? "",
            Group = (string?)element.Attribute("group") ?? "",
            Permission = (string?)element.Attribute("perm") ?? "ro",
            Rule = ParseRule((string?)element.Attribute("rule")),
            State = ParseState((string?)element.Attribute("state")) ?? IndiState.Idle,
        };

        foreach (var child in element.Elements())
        {
            var name = (string?)child.Attribute("name");
            if (name is null) continue;
            property.Elements[name] = new IndiElement
            {
                Name = name,
                Label = (string?)child.Attribute("label") ?? name,
                Text = child.Value.Trim(),
                Format = (string?)child.Attribute("format"),
                Min = ParseOptionalDouble((string?)child.Attribute("min")),
                Max = ParseOptionalDouble((string?)child.Attribute("max")),
                Step = ParseOptionalDouble((string?)child.Attribute("step")),
            };
        }
        return new IndiDefineMessage(property, message);
    }

    private static IndiSetMessage? ParseSet(XElement element, string typeName, string device, string? message)
    {
        if (!TryParseType(typeName, out var type)) return null;

        var values = new List<IndiElementValue>();
        foreach (var child in element.Elements())
        {
            var name = (string?)child.Attribute("name");
            if (name is null) continue;

            if (type == IndiPropertyType.Blob)
            {
                var format = (string?)child.Attribute("format") ?? "";
                var size = ParseOptionalDouble((string?)child.Attribute("size"));
                byte[]? data = null;
                if (size > 0)
                {
                    // Convert.FromBase64String tolerates the line breaks INDI inserts.
                    data = Convert.FromBase64String(child.Value);
                    if (format.EndsWith(".z", StringComparison.OrdinalIgnoreCase))
                    {
                        data = Inflate(data);
                        format = format[..^2];
                    }
                }
                values.Add(new IndiElementValue(name, "", data, format));
            }
            else
            {
                values.Add(new IndiElementValue(name, child.Value.Trim()));
            }
        }

        return new IndiSetMessage(device, (string?)element.Attribute("name") ?? "", type,
            ParseState((string?)element.Attribute("state")), values, message);
    }

    // Client -> server messages.

    public static XElement GetProperties(string? device = null, string? name = null)
    {
        var e = new XElement("getProperties", new XAttribute("version", "1.7"));
        if (device is not null) e.Add(new XAttribute("device", device));
        if (name is not null) e.Add(new XAttribute("name", name));
        return e;
    }

    public static XElement EnableBlob(string device, string mode = "Also") =>
        new("enableBLOB", new XAttribute("device", device), mode);

    public static XElement NewNumbers(string device, string name, IEnumerable<KeyValuePair<string, double>> values) =>
        new("newNumberVector",
            new XAttribute("device", device), new XAttribute("name", name),
            values.Select(v => new XElement("oneNumber", new XAttribute("name", v.Key),
                v.Value.ToString("R", CultureInfo.InvariantCulture))));

    public static XElement NewSwitches(string device, string name, IEnumerable<KeyValuePair<string, bool>> values) =>
        new("newSwitchVector",
            new XAttribute("device", device), new XAttribute("name", name),
            values.Select(v => new XElement("oneSwitch", new XAttribute("name", v.Key), v.Value ? "On" : "Off")));

    public static XElement NewTexts(string device, string name, IEnumerable<KeyValuePair<string, string>> values) =>
        new("newTextVector",
            new XAttribute("device", device), new XAttribute("name", name),
            values.Select(v => new XElement("oneText", new XAttribute("name", v.Key), v.Value)));

    private static bool TryParseType(string typeName, out IndiPropertyType type)
    {
        type = typeName switch
        {
            "Number" => IndiPropertyType.Number,
            "Switch" => IndiPropertyType.Switch,
            "Text" => IndiPropertyType.Text,
            "Light" => IndiPropertyType.Light,
            "BLOB" => IndiPropertyType.Blob,
            _ => (IndiPropertyType)(-1),
        };
        return (int)type >= 0;
    }

    private static IndiState? ParseState(string? s) => s switch
    {
        "Idle" => IndiState.Idle,
        "Ok" => IndiState.Ok,
        "Busy" => IndiState.Busy,
        "Alert" => IndiState.Alert,
        _ => null,
    };

    private static IndiSwitchRule ParseRule(string? s) => s switch
    {
        "OneOfMany" => IndiSwitchRule.OneOfMany,
        "AtMostOne" => IndiSwitchRule.AtMostOne,
        _ => IndiSwitchRule.AnyOfMany,
    };

    private static double ParseOptionalDouble(string? s) =>
        s is not null && IndiNumber.TryParse(s, out var d) ? d : 0;

    private static byte[] Inflate(byte[] data)
    {
        using var input = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }
}

/// <summary>INDI numbers may be decimal ("5.5") or sexagesimal ("05:30:00", "-05 30").</summary>
public static partial class IndiNumber
{
    [System.Text.RegularExpressions.GeneratedRegex(@"%[-+ 0#']*\d*(?:\.(\d+))?([a-zA-Z])")]
    private static partial System.Text.RegularExpressions.Regex FormatPattern();

    /// <summary>
    /// Formats a value using the driver's printf-style format. INDI adds "%m" for sexagesimal, where the
    /// precision picks the resolution as in libindi's numberFormat: 9 = h:mm:ss.ss, 8 = h:mm:ss.s,
    /// 6 = h:mm:ss, 5 = h:mm.m, anything else = h:mm.
    /// </summary>
    public static string Format(double value, string? format)
    {
        var match = format is null ? null : FormatPattern().Match(format);
        if (match is null || !match.Success) return value.ToString("G", CultureInfo.InvariantCulture);

        var precision = match.Groups[1].Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : -1;
        return char.ToLowerInvariant(match.Groups[2].Value[0]) switch
        {
            'f' => value.ToString("F" + (precision < 0 ? 6 : precision), CultureInfo.InvariantCulture),
            'd' or 'i' or 'u' => Math.Round(value).ToString("F0", CultureInfo.InvariantCulture),
            'e' => value.ToString(precision < 0 ? "E6" : "E" + precision, CultureInfo.InvariantCulture),
            'm' => FormatSexagesimal(value, precision),
            _ => value.ToString(precision <= 0 ? "G" : "G" + precision, CultureInfo.InvariantCulture),
        };
    }

    private static string FormatSexagesimal(double value, int precision)
    {
        // Work in whole display units so rounding can't produce "60" seconds.
        long unitsPerWhole = precision switch { 9 => 360_000, 8 => 36_000, 6 => 3_600, 5 => 600, _ => 60 };
        var total = (long)Math.Round(Math.Abs(value) * unitsPerWhole);
        var sign = value < 0 && total > 0 ? "-" : "";
        var whole = total / unitsPerWhole;
        var rest = total % unitsPerWhole;
        if (unitsPerWhole == 60) return $"{sign}{whole}:{rest:00}";
        if (unitsPerWhole == 600) return $"{sign}{whole}:{rest / 10:00}.{rest % 10}";

        var unitsPerSecond = unitsPerWhole / 3_600;
        var minutes = rest / (unitsPerSecond * 60);
        var secondUnits = rest % (unitsPerSecond * 60);
        var seconds = secondUnits / unitsPerSecond;
        var fraction = secondUnits % unitsPerSecond;
        var digits = (int)Math.Log10(unitsPerSecond);
        var fractionText = digits > 0 ? "." + fraction.ToString(new string('0', digits), CultureInfo.InvariantCulture) : "";
        return $"{sign}{whole}:{minutes:00}:{seconds:00}{fractionText}";
    }

    public static double Parse(string text) =>
        TryParse(text, out var v) ? v : throw new FormatException($"Not an INDI number: '{text}'");

    public static bool TryParse(string text, out double value)
    {
        var s = text.Trim();
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return true;

        var negative = s.StartsWith('-');
        if (negative || s.StartsWith('+')) s = s[1..];
        var parts = s.Split([':', ' ', ';'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is 0 or > 3) return false;

        double result = 0, scale = 1;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var p)) return false;
            result += p / scale;
            scale *= 60;
        }
        value = negative ? -result : result;
        return true;
    }
}
