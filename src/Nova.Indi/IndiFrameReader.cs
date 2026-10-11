namespace Nova.Indi;

/// <summary>
/// Splits the INDI byte stream into complete top-level XML elements.
/// XmlReader can't be used directly on the socket: it reads past the end of each element to find the
/// next node, so the last message before a pause in traffic (often an image) would be held back
/// until the server sent something else. This only tracks tag nesting; real XML parsing happens per element.
/// </summary>
internal sealed class IndiFrameReader(Stream stream)
{
    private enum State { Text, TagStart, OpenTag, CloseTag, Special }

    private readonly byte[] _buffer = new byte[64 * 1024];
    private readonly MemoryStream _element = new();
    private int _position, _length, _segmentStart;
    private bool _inElement;
    private int _depth;
    private State _state = State.Text;
    private byte _quote, _previous;

    /// <summary>Returns the bytes of the next complete element, or null when the stream ends.</summary>
    public async Task<byte[]?> ReadElementAsync(CancellationToken ct)
    {
        while (true)
        {
            if (_position >= _length)
            {
                if (_inElement) _element.Write(_buffer, _segmentStart, _length - _segmentStart);
                _length = await stream.ReadAsync(_buffer, ct);
                _position = _segmentStart = 0;
                if (_length == 0) return null;
            }

            var b = _buffer[_position++];
            switch (_state)
            {
                case State.Text:
                    if (b == '<')
                    {
                        if (!_inElement)
                        {
                            _inElement = true;
                            _segmentStart = _position - 1;
                        }
                        _state = State.TagStart;
                    }
                    break;

                case State.TagStart:
                    _state = b switch
                    {
                        (byte)'/' => State.CloseTag,
                        (byte)'?' or (byte)'!' => State.Special,
                        _ => State.OpenTag,
                    };
                    _previous = b;
                    break;

                case State.OpenTag:
                    if (_quote != 0)
                    {
                        if (b == _quote) _quote = 0;
                    }
                    else if (b is (byte)'"' or (byte)'\'')
                    {
                        _quote = b;
                    }
                    else if (b == '>')
                    {
                        _state = State.Text;
                        if (_previous == '/') // self-closing, e.g. <getProperties version="1.7"/>
                        {
                            if (_depth == 0) return Complete();
                        }
                        else
                        {
                            _depth++;
                        }
                    }
                    _previous = b;
                    break;

                case State.CloseTag:
                    if (b == '>')
                    {
                        _state = State.Text;
                        if (--_depth <= 0)
                        {
                            _depth = 0;
                            return Complete();
                        }
                    }
                    break;

                case State.Special: // <?xml ...?> or <!-- ... -->, skipped
                    if (b == '>')
                    {
                        _state = State.Text;
                        if (_depth == 0) Discard();
                    }
                    break;
            }
        }
    }

    private byte[] Complete()
    {
        _element.Write(_buffer, _segmentStart, _position - _segmentStart);
        var result = _element.ToArray();
        Discard();
        return result;
    }

    private void Discard()
    {
        _element.SetLength(0);
        _inElement = false;
        _segmentStart = _position;
    }
}
