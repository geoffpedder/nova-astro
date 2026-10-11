using System.Net.Sockets;
using System.Xml;
using System.Xml.Linq;

namespace Nova.Indi;

/// <summary>
/// TCP client for an INDI server (indiserver). Keeps a live mirror of every device property
/// and raises events as the server defines, updates and deletes them.
/// Events fire on the socket reader thread, so handlers must be quick and must not block.
/// </summary>
public sealed class IndiClient : IAsyncDisposable
{
    public const int DefaultPort = 7624;

    private readonly object _sync = new();
    private readonly Dictionary<string, Dictionary<string, IndiProperty>> _devices = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private CancellationTokenSource? _readCts;
    private Task? _readLoop;

    public bool IsConnected => _readLoop is { IsCompleted: false };

    public event EventHandler<IndiProperty>? PropertyDefined;
    public event EventHandler<IndiProperty>? PropertyUpdated;
    public event EventHandler<IndiDeleteMessage>? PropertyDeleted;
    /// <summary>Human-readable messages from drivers (the "message" attribute or element).</summary>
    public event EventHandler<IndiMessage>? MessageReceived;
    /// <summary>The connection closed. The argument is the error, or null for a clean close.</summary>
    public event EventHandler<Exception?>? Disconnected;

    public async Task ConnectAsync(string host, int port, CancellationToken ct)
    {
        if (IsConnected) throw new InvalidOperationException("Already connected");

        // Reconnecting after the server went away: drop the old socket and stale properties.
        _tcp?.Dispose();
        lock (_sync) _devices.Clear();

        var tcp = new TcpClient { NoDelay = true, ReceiveBufferSize = 4 * 1024 * 1024 };
        try
        {
            await tcp.ConnectAsync(host, port, ct);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }

        _tcp = tcp;
        _stream = tcp.GetStream();
        _readCts = new CancellationTokenSource();
        _readLoop = Task.Run(() => ReadLoopAsync(_stream, _readCts.Token));
        await SendAsync(IndiParser.GetProperties(), ct);
    }

    public async Task DisconnectAsync()
    {
        _readCts?.Cancel();
        _tcp?.Dispose();
        if (_readLoop is not null)
        {
            try { await _readLoop; } catch { /* already reported via Disconnected */ }
        }
        _tcp = null;
        _stream = null;
        _readLoop = null;
        lock (_sync) _devices.Clear();
    }

    public ValueTask DisposeAsync() => new(DisconnectAsync());

    // ---- Reading the property mirror ----

    public IReadOnlyList<string> DeviceNames
    {
        get { lock (_sync) return _devices.Keys.ToList(); }
    }

    public IReadOnlyList<IndiProperty> GetProperties(string device)
    {
        lock (_sync) return _devices.TryGetValue(device, out var props) ? props.Values.ToList() : [];
    }

    public IndiProperty? GetProperty(string device, string name)
    {
        lock (_sync) return _devices.GetValueOrDefault(device)?.GetValueOrDefault(name);
    }

    /// <summary>
    /// Waits until a property satisfies <paramref name="predicate"/>. With <paramref name="checkCurrent"/>
    /// false, only updates arriving after the call count, which is how to wait for the reaction to a command.
    /// </summary>
    public async Task<IndiProperty> WaitForAsync(string device, string name, Func<IndiProperty, bool> predicate,
        TimeSpan timeout, CancellationToken ct, bool checkCurrent = true)
    {
        var tcs = new TaskCompletionSource<IndiProperty>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnProperty(object? sender, IndiProperty p)
        {
            if (p.Device == device && p.Name == name && predicate(p)) tcs.TrySetResult(p);
        }
        void OnDisconnected(object? sender, Exception? error) =>
            tcs.TrySetException(new IOException("INDI server disconnected", error));

        // Subscribe before checking the current value so no update can slip between the two.
        PropertyDefined += OnProperty;
        PropertyUpdated += OnProperty;
        Disconnected += OnDisconnected;
        try
        {
            if (checkCurrent && GetProperty(device, name) is { } current && predicate(current)) return current;
            try
            {
                return await tcs.Task.WaitAsync(timeout, ct);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException($"Timed out after {timeout.TotalSeconds:0}s waiting for {device}.{name}");
            }
        }
        finally
        {
            PropertyDefined -= OnProperty;
            PropertyUpdated -= OnProperty;
            Disconnected -= OnDisconnected;
        }
    }

    // ---- Sending commands ----

    public Task SetNumbersAsync(string device, string name, CancellationToken ct, params (string Element, double Value)[] values) =>
        SendAsync(IndiParser.NewNumbers(device, name, values.Select(v => KeyValuePair.Create(v.Element, v.Value))), ct);

    public Task SetSwitchesAsync(string device, string name, CancellationToken ct, params (string Element, bool On)[] values) =>
        SendAsync(IndiParser.NewSwitches(device, name, values.Select(v => KeyValuePair.Create(v.Element, v.On))), ct);

    public Task SetTextsAsync(string device, string name, CancellationToken ct, params (string Element, string Value)[] values) =>
        SendAsync(IndiParser.NewTexts(device, name, values.Select(v => KeyValuePair.Create(v.Element, v.Value))), ct);

    /// <summary>Images only arrive for devices that have BLOBs enabled ("Also" = BLOBs plus normal traffic).</summary>
    public Task EnableBlobAsync(string device, CancellationToken ct, string mode = "Also") =>
        SendAsync(IndiParser.EnableBlob(device, mode), ct);

    public async Task SendAsync(XElement message, CancellationToken ct)
    {
        var stream = _stream ?? throw new InvalidOperationException("Not connected to an INDI server");
        var bytes = System.Text.Encoding.UTF8.GetBytes(message.ToString(SaveOptions.DisableFormatting) + "\n");
        await _writeLock.WaitAsync(ct);
        try
        {
            await stream.WriteAsync(bytes, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ---- Reading from the socket ----

    private async Task ReadLoopAsync(Stream stream, CancellationToken ct)
    {
        Exception? error = null;
        try
        {
            var settings = new XmlReaderSettings
            {
                IgnoreWhitespace = true,
                IgnoreComments = true,
                DtdProcessing = DtdProcessing.Prohibit,
                CheckCharacters = false,
            };
            var frames = new IndiFrameReader(stream);
            // A null frame means the server closed the connection.
            while (await frames.ReadElementAsync(ct) is { } frame)
            {
                try
                {
                    using var reader = XmlReader.Create(new MemoryStream(frame), settings);
                    Apply(IndiParser.Parse(XElement.Load(reader)));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One bad message (or a throwing event handler) must not kill the connection.
                    MessageReceived?.Invoke(this, new IndiTextMessage("", $"Ignored INDI message: {ex.Message}"));
                }
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            error = ex;
        }
        catch
        {
            // Cancelled by DisconnectAsync: a clean close.
        }
        Disconnected?.Invoke(this, error);
    }

    private void Apply(IndiMessage? message)
    {
        switch (message)
        {
            case IndiDefineMessage def:
            {
                lock (_sync)
                {
                    if (!_devices.TryGetValue(def.Device, out var props))
                        _devices[def.Device] = props = new Dictionary<string, IndiProperty>(StringComparer.Ordinal);
                    props[def.Property.Name] = def.Property;
                }
                PropertyDefined?.Invoke(this, def.Property);
                break;
            }
            case IndiSetMessage set:
            {
                IndiProperty? property;
                lock (_sync)
                {
                    property = _devices.GetValueOrDefault(set.Device)?.GetValueOrDefault(set.Name);
                    if (property is not null)
                    {
                        if (set.State is { } state) property.State = state;
                        property.LastUpdatedUtc = DateTime.UtcNow;
                        foreach (var value in set.Values)
                        {
                            if (!property.Elements.TryGetValue(value.Name, out var element)) continue;
                            element.Text = value.Text;
                            if (property.Type == IndiPropertyType.Blob)
                            {
                                element.BlobData = value.BlobData;
                                element.BlobFormat = value.BlobFormat;
                            }
                        }
                    }
                }
                // A set for a property we never saw defined is a protocol error; ignore it.
                if (property is not null) PropertyUpdated?.Invoke(this, property);
                break;
            }
            case IndiDeleteMessage del:
            {
                lock (_sync)
                {
                    if (del.Name is null) _devices.Remove(del.Device);
                    else _devices.GetValueOrDefault(del.Device)?.Remove(del.Name);
                }
                PropertyDeleted?.Invoke(this, del);
                break;
            }
        }

        if (!string.IsNullOrWhiteSpace(message?.Message)) MessageReceived?.Invoke(this, message);
    }
}
