using System;
using System.Globalization;
using System.Text;

namespace Basis.ModelPickup.Validation
{
    public struct BasisJsonReaderLimits
    {
        public int MaxDepth, MaxTokens, MaxKeyBytes, MaxObjectKeys, MaxNumberChars;

        public static BasisJsonReaderLimits From(in BasisModelLimits limits)
        {
            return new BasisJsonReaderLimits
            {
                MaxDepth = limits.MaxJsonDepth,
                MaxTokens = limits.MaxJsonTokens,
                MaxKeyBytes = limits.MaxJsonKeyBytes,
                MaxObjectKeys = limits.MaxJsonObjectKeys,
                MaxNumberChars = limits.MaxJsonNumberChars,
            };
        }
    }

    public enum BasisJsonTokenKind : byte
    {
        None,
        BeginObject,
        EndObject,
        BeginArray,
        EndArray,
        PropertyName,
        String,
        Number,
        True,
        False,
        Null,
        End,
    }

    /// <summary>
    /// Strict, bounded RFC 8259 pull reader over UTF-8 bytes. Iterative (no recursion), so a depth bomb cannot
    /// overflow the stack; strings are scanned once and only materialised on request; every object's keys are
    /// checked for duplicates, including inside skipped subtrees. Callers validate UTF-8 with <see cref="ValidateUtf8"/>
    /// first. Error text carries byte offsets only, never input bytes.
    /// </summary>
    public sealed class BasisJsonReader
    {
        private const byte StateRootValue = 0;
        private const byte StateRootDone = 1;
        private const byte StateObjectFirst = 2;
        private const byte StateObjectValue = 3;
        private const byte StateObjectAfterValue = 4;
        private const byte StateArrayFirst = 5;
        private const byte StateArrayValue = 6;
        private const byte StateArrayAfterValue = 7;

        private const ulong FnvOffset = 14695981039346656037UL;
        private const ulong FnvPrime = 1099511628211UL;

        /// <summary>The bytes being read; the reader never writes them.</summary>
        public readonly byte[] Buffer;
        private readonly int _start;
        private readonly int _end;
        private readonly BasisJsonReaderLimits _limits;
        private readonly bool _allowTrailingNul;
        private readonly byte[] _states;
        private readonly ulong[] _keyHashes;
        private readonly int[] _keyCounts;
        private readonly byte[] _keyScratch;
        private int _keyLength;
        private int _pos;

        public BasisJsonTokenKind Kind;
        public int ValueStart;
        public int ValueLength;
        public bool ValueHasEscapes;
        public int Depth;
        public int TokenCount;
        public int ErrorOffset;
        public string Error;
        /// <summary>True when <see cref="Error"/> is a bound (depth, tokens, key count or length, number length), not a syntax error.</summary>
        public bool ErrorIsLimit;

        public BasisJsonReader(byte[] buffer, int offset, int count, in BasisJsonReaderLimits limits, bool allowTrailingNul)
        {
            Buffer = buffer;
            _start = offset;
            _end = offset + count;
            _pos = offset;
            _limits = limits;
            _allowTrailingNul = allowTrailingNul;
            _states = new byte[limits.MaxDepth + 1];
            _keyCounts = new int[limits.MaxDepth + 1];
            _keyHashes = new ulong[(limits.MaxDepth + 1) * limits.MaxObjectKeys];
            _keyScratch = new byte[limits.MaxKeyBytes];
            _states[0] = StateRootValue;
        }

        /// <summary>Decoded bytes of the current property name (valid until the next key is read).</summary>
        public ReadOnlySpan<byte> PropertyName => new ReadOnlySpan<byte>(_keyScratch, 0, _keyLength);

        /// <summary>Byte offset of the current token, relative to the start of the JSON.</summary>
        public int TokenOffset => ValueStart - _start;

        public bool PropertyNameIs(string ascii)
        {
            if (Kind != BasisJsonTokenKind.PropertyName || ascii.Length != _keyLength) return false;
            for (int i = 0; i < _keyLength; i++)
            {
                if (_keyScratch[i] != ascii[i]) return false;
            }
            return true;
        }

        /// <summary>Advances to the next token. False when an error was set; the token after the root value is <see cref="BasisJsonTokenKind.End"/>.</summary>
        public bool Read()
        {
            if (Error != null) return false;
            if (Kind == BasisJsonTokenKind.End) return true;
            SkipWhitespace();
            switch (_states[Depth])
            {
                case StateRootValue:
                case StateObjectValue:
                case StateArrayValue:
                    return ReadValue();
                case StateRootDone:
                    return ReadEnd();
                case StateObjectFirst:
                    if (_pos >= _end) return Fail("unexpected end of JSON");
                    if (Buffer[_pos] == (byte)'}') return Close(BasisJsonTokenKind.EndObject);
                    if (Buffer[_pos] == (byte)'"') return ReadKey();
                    return Fail("expected a property name or '}'");
                case StateObjectAfterValue:
                    if (_pos >= _end) return Fail("unexpected end of JSON");
                    if (Buffer[_pos] == (byte)',')
                    {
                        _pos++;
                        SkipWhitespace();
                        if (_pos >= _end || Buffer[_pos] != (byte)'"') return Fail("expected a property name");
                        return ReadKey();
                    }
                    if (Buffer[_pos] == (byte)'}') return Close(BasisJsonTokenKind.EndObject);
                    return Fail("expected ',' or '}'");
                case StateArrayFirst:
                    if (_pos >= _end) return Fail("unexpected end of JSON");
                    if (Buffer[_pos] == (byte)']') return Close(BasisJsonTokenKind.EndArray);
                    return ReadValue();
                case StateArrayAfterValue:
                    if (_pos >= _end) return Fail("unexpected end of JSON");
                    if (Buffer[_pos] == (byte)',')
                    {
                        _pos++;
                        SkipWhitespace();
                        _states[Depth] = StateArrayValue;
                        return ReadValue();
                    }
                    if (Buffer[_pos] == (byte)']') return Close(BasisJsonTokenKind.EndArray);
                    return Fail("expected ',' or ']'");
                default:
                    return Fail("internal reader state");
            }
        }

        /// <summary>
        /// Skips the value that starts at the current token. Scalars are already consumed; containers are read to their
        /// close, so every nested key is still duplicate-checked and every token still counted.
        /// </summary>
        public bool TrySkipValue()
        {
            if (Error != null) return false;
            switch (Kind)
            {
                case BasisJsonTokenKind.BeginObject:
                case BasisJsonTokenKind.BeginArray:
                    int target = Depth - 1;
                    while (Depth > target)
                    {
                        if (!Read()) return false;
                        if (Kind == BasisJsonTokenKind.End) return Fail("unexpected end of JSON");
                    }
                    return true;
                case BasisJsonTokenKind.String:
                case BasisJsonTokenKind.Number:
                case BasisJsonTokenKind.True:
                case BasisJsonTokenKind.False:
                case BasisJsonTokenKind.Null:
                    return true;
                default:
                    return Fail("expected a value");
            }
        }

        /// <summary>Grammar 0|[1-9][0-9]{0,9}, at most int.MaxValue. No sign, fraction or exponent.</summary>
        public bool TryGetNonNegativeInt32(out int value)
        {
            value = 0;
            if (!TryGetDigits(out long parsed) || parsed > int.MaxValue) return false;
            value = (int)parsed;
            return true;
        }

        public bool TryGetUInt32(out uint value)
        {
            value = 0;
            if (!TryGetDigits(out long parsed) || parsed > uint.MaxValue) return false;
            value = (uint)parsed;
            return true;
        }

        private bool TryGetDigits(out long value)
        {
            value = 0;
            if (Kind != BasisJsonTokenKind.Number || ValueLength < 1 || ValueLength > 10) return false;
            if (Buffer[ValueStart] == (byte)'0' && ValueLength > 1) return false;
            for (int i = 0; i < ValueLength; i++)
            {
                byte c = Buffer[ValueStart + i];
                if (c < (byte)'0' || c > (byte)'9') return false;
                value = value * 10 + (c - (byte)'0');
            }
            return true;
        }

        /// <summary>Parses as double (invariant), then narrows to float. Both must be finite. -0 becomes +0.</summary>
        public bool TryGetSingle(out float value)
        {
            value = 0f;
            if (Kind != BasisJsonTokenKind.Number || ValueLength < 1 || ValueLength > 64) return false;
            Span<char> chars = stackalloc char[64];
            for (int i = 0; i < ValueLength; i++)
            {
                chars[i] = (char)Buffer[ValueStart + i];
            }
            // Older runtimes report overflow as a failed parse; newer ones return infinity. Both are rejected.
            if (!double.TryParse(chars.Slice(0, ValueLength), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                || !BasisGlbNumbers.IsFinite(parsed))
            {
                return false;
            }
            float narrowed = (float)parsed;
            if (!BasisGlbNumbers.IsFinite(narrowed)) return false;
            value = BasisGlbNumbers.PositiveZero(narrowed);
            return true;
        }

        public bool TryGetBoolean(out bool value)
        {
            value = Kind == BasisJsonTokenKind.True;
            return Kind == BasisJsonTokenKind.True || Kind == BasisJsonTokenKind.False;
        }

        /// <summary>Decodes the current string. False when it is not a string or decodes to more than <paramref name="maxUtf8Bytes"/>.</summary>
        public bool TryGetString(int maxUtf8Bytes, out string value)
        {
            value = null;
            if (Kind != BasisJsonTokenKind.String) return false;
            if (!ValueHasEscapes)
            {
                if (ValueLength > maxUtf8Bytes) return false;
                value = Encoding.UTF8.GetString(Buffer, ValueStart, ValueLength);
                return true;
            }
            var decoded = new byte[ValueLength];
            int length = DecodeString(ValueStart, ValueLength, decoded);
            if (length < 0 || length > maxUtf8Bytes) return false;
            value = Encoding.UTF8.GetString(decoded, 0, length);
            return true;
        }

        /// <summary>Unescapes the current string into <paramref name="destination"/>, which must hold at least <see cref="ValueLength"/> bytes.</summary>
        public bool TryCopyUnescapedString(byte[] destination, out int length)
        {
            length = 0;
            if (Kind != BasisJsonTokenKind.String || destination == null || destination.Length < ValueLength) return false;
            if (!ValueHasEscapes)
            {
                System.Buffer.BlockCopy(Buffer, ValueStart, destination, 0, ValueLength);
                length = ValueLength;
                return true;
            }
            length = DecodeString(ValueStart, ValueLength, destination);
            return length >= 0;
        }

        /// <summary>
        /// Strict UTF-8: rejects overlong forms, surrogate code points, values above U+10FFFF, stray continuation bytes
        /// and truncated sequences.
        /// </summary>
        public static bool ValidateUtf8(byte[] data, int offset, int count, out int errorOffset)
        {
            errorOffset = -1;
            int end = offset + count;
            int i = offset;
            while (i < end)
            {
                byte b = data[i];
                if (b < 0x80)
                {
                    i++;
                    continue;
                }
                int need;
                byte lo = 0x80, hi = 0xBF;
                if (b >= 0xC2 && b <= 0xDF) need = 1;
                else if (b == 0xE0) { need = 2; lo = 0xA0; }
                else if ((b >= 0xE1 && b <= 0xEC) || b == 0xEE || b == 0xEF) need = 2;
                else if (b == 0xED) { need = 2; hi = 0x9F; }
                else if (b == 0xF0) { need = 3; lo = 0x90; }
                else if (b >= 0xF1 && b <= 0xF3) need = 3;
                else if (b == 0xF4) { need = 3; hi = 0x8F; }
                else
                {
                    errorOffset = i - offset;
                    return false;
                }
                if (i + need >= end)
                {
                    errorOffset = i - offset;
                    return false;
                }
                byte second = data[i + 1];
                if (second < lo || second > hi)
                {
                    errorOffset = i - offset;
                    return false;
                }
                for (int k = 2; k <= need; k++)
                {
                    byte next = data[i + k];
                    if (next < 0x80 || next > 0xBF)
                    {
                        errorOffset = i - offset;
                        return false;
                    }
                }
                i += need + 1;
            }
            return true;
        }

        private void SkipWhitespace()
        {
            while (_pos < _end)
            {
                byte c = Buffer[_pos];
                if (c == (byte)' ' || c == (byte)'\n' || c == (byte)'\r' || c == (byte)'\t')
                {
                    _pos++;
                    continue;
                }
                return;
            }
        }

        private byte AfterValueState()
        {
            switch (_states[Depth])
            {
                case StateRootValue:
                    return StateRootDone;
                case StateObjectValue:
                    return StateObjectAfterValue;
                default:
                    return StateArrayAfterValue;
            }
        }

        private bool ReadValue()
        {
            if (_pos >= _end) return Fail("unexpected end of JSON; expected a value");
            byte c = Buffer[_pos];
            byte after = AfterValueState();
            switch (c)
            {
                case (byte)'{':
                case (byte)'[':
                    if (Depth + 1 > _limits.MaxDepth)
                    {
                        return FailLimit("JSON nesting is deeper than " + _limits.MaxDepth.ToString(CultureInfo.InvariantCulture) + " levels.");
                    }
                    _states[Depth] = after;
                    ValueStart = _pos;
                    ValueLength = 1;
                    _pos++;
                    Depth++;
                    if (c == (byte)'{')
                    {
                        _states[Depth] = StateObjectFirst;
                        _keyCounts[Depth] = 0;
                        Kind = BasisJsonTokenKind.BeginObject;
                    }
                    else
                    {
                        _states[Depth] = StateArrayFirst;
                        Kind = BasisJsonTokenKind.BeginArray;
                    }
                    return CountToken();
                case (byte)'"':
                    if (!ScanString()) return false;
                    _states[Depth] = after;
                    Kind = BasisJsonTokenKind.String;
                    return CountToken();
                case (byte)'t':
                    if (!ScanLiteral("true")) return false;
                    _states[Depth] = after;
                    Kind = BasisJsonTokenKind.True;
                    return CountToken();
                case (byte)'f':
                    if (!ScanLiteral("false")) return false;
                    _states[Depth] = after;
                    Kind = BasisJsonTokenKind.False;
                    return CountToken();
                case (byte)'n':
                    if (!ScanLiteral("null")) return false;
                    _states[Depth] = after;
                    Kind = BasisJsonTokenKind.Null;
                    return CountToken();
                default:
                    if (c == (byte)'-' || (c >= (byte)'0' && c <= (byte)'9'))
                    {
                        if (!ScanNumber()) return false;
                        _states[Depth] = after;
                        Kind = BasisJsonTokenKind.Number;
                        return CountToken();
                    }
                    return Fail("expected a value");
            }
        }

        private bool Close(BasisJsonTokenKind kind)
        {
            ValueStart = _pos;
            ValueLength = 1;
            _pos++;
            Depth--;
            Kind = kind;
            return CountToken();
        }

        private bool ReadEnd()
        {
            if (_pos == _end)
            {
                Kind = BasisJsonTokenKind.End;
                ValueStart = _pos;
                ValueLength = 0;
                return true;
            }
            if (_allowTrailingNul && _end - _pos <= 3)
            {
                for (int i = _pos; i < _end; i++)
                {
                    if (Buffer[i] != 0) return Fail("unexpected data after the JSON value");
                }
                _pos = _end;
                Kind = BasisJsonTokenKind.End;
                ValueStart = _pos;
                ValueLength = 0;
                return true;
            }
            return Fail("unexpected data after the JSON value");
        }

        private bool ReadKey()
        {
            int keyOffset = _pos;
            if (!ScanString()) return false;
            int length = ValueHasEscapes
                ? DecodeString(ValueStart, ValueLength, _keyScratch)
                : (ValueLength <= _keyScratch.Length ? CopyKey() : -1);
            if (length < 0)
            {
                return FailLimit("JSON property name at byte " + BasisGlbErrors.N(keyOffset - _start) + " is longer than "
                    + BasisGlbErrors.N(_limits.MaxKeyBytes) + " bytes.");
            }
            _keyLength = length;
            ulong hash = FnvOffset;
            for (int i = 0; i < length; i++)
            {
                hash = (hash ^ _keyScratch[i]) * FnvPrime;
            }
            int count = _keyCounts[Depth];
            int slot = Depth * _limits.MaxObjectKeys;
            for (int i = 0; i < count; i++)
            {
                // A 64-bit collision can only cause a false rejection, never a missed duplicate.
                if (_keyHashes[slot + i] == hash)
                {
                    return FailPlain("JSON object at byte " + BasisGlbErrors.N(keyOffset - _start) + " repeats a key.");
                }
            }
            if (count >= _limits.MaxObjectKeys)
            {
                return FailLimit("JSON object at byte " + BasisGlbErrors.N(keyOffset - _start) + " has more than "
                    + BasisGlbErrors.N(_limits.MaxObjectKeys) + " keys.");
            }
            _keyHashes[slot + count] = hash;
            _keyCounts[Depth] = count + 1;
            SkipWhitespace();
            if (_pos >= _end || Buffer[_pos] != (byte)':') return Fail("expected ':'");
            _pos++;
            _states[Depth] = StateObjectValue;
            Kind = BasisJsonTokenKind.PropertyName;
            return CountToken();
        }

        private int CopyKey()
        {
            System.Buffer.BlockCopy(Buffer, ValueStart, _keyScratch, 0, ValueLength);
            return ValueLength;
        }

        private bool ScanString()
        {
            int quote = _pos;
            _pos++;
            int start = _pos;
            bool escapes = false;
            while (true)
            {
                if (_pos >= _end)
                {
                    _pos = quote;
                    return Fail("unterminated string");
                }
                byte c = Buffer[_pos];
                if (c == (byte)'"')
                {
                    ValueStart = start;
                    ValueLength = _pos - start;
                    ValueHasEscapes = escapes;
                    _pos++;
                    return true;
                }
                if (c < 0x20) return Fail("control character in a string");
                if (c != (byte)'\\')
                {
                    _pos++;
                    continue;
                }
                escapes = true;
                if (_pos + 1 >= _end) return Fail("unterminated escape");
                byte e = Buffer[_pos + 1];
                switch (e)
                {
                    case (byte)'"':
                    case (byte)'\\':
                    case (byte)'/':
                    case (byte)'b':
                    case (byte)'f':
                    case (byte)'n':
                    case (byte)'r':
                    case (byte)'t':
                        _pos += 2;
                        break;
                    case (byte)'u':
                        int unit = ReadHex4(_pos + 2);
                        if (unit < 0) return Fail("invalid \\u escape");
                        if (unit >= 0xDC00 && unit <= 0xDFFF) return Fail("unpaired surrogate escape");
                        if (unit >= 0xD800 && unit <= 0xDBFF)
                        {
                            if (_pos + 12 > _end || Buffer[_pos + 6] != (byte)'\\' || Buffer[_pos + 7] != (byte)'u')
                            {
                                return Fail("unpaired surrogate escape");
                            }
                            int low = ReadHex4(_pos + 8);
                            if (low < 0xDC00 || low > 0xDFFF) return Fail("unpaired surrogate escape");
                            _pos += 12;
                        }
                        else
                        {
                            _pos += 6;
                        }
                        break;
                    default:
                        return Fail("invalid escape");
                }
            }
        }

        private int ReadHex4(int at)
        {
            return ReadHex4(Buffer, at, _end);
        }

        private static int ReadHex4(byte[] buffer, int at, int end)
        {
            if (at + 4 > end) return -1;
            int value = 0;
            for (int i = 0; i < 4; i++)
            {
                int digit = HexValue(buffer[at + i]);
                if (digit < 0) return -1;
                value = (value << 4) | digit;
            }
            return value;
        }

        private static int HexValue(byte c)
        {
            if (c >= (byte)'0' && c <= (byte)'9') return c - (byte)'0';
            if (c >= (byte)'a' && c <= (byte)'f') return c - (byte)'a' + 10;
            if (c >= (byte)'A' && c <= (byte)'F') return c - (byte)'A' + 10;
            return -1;
        }

        private int DecodeString(int start, int length, byte[] destination)
        {
            return DecodeEscaped(Buffer, start, length, destination);
        }

        /// <summary>
        /// Decodes a string range that a reader has already scanned (so every escape is known to be well formed).
        /// Returns the byte count, or -1 if it does not fit.
        /// </summary>
        public static int DecodeEscaped(byte[] source, int start, int length, byte[] destination)
        {
            int written = 0;
            int end = start + length;
            int i = start;
            while (i < end)
            {
                byte c = source[i];
                if (c != (byte)'\\')
                {
                    if (written >= destination.Length) return -1;
                    destination[written++] = c;
                    i++;
                    continue;
                }
                byte e = source[i + 1];
                int codePoint;
                switch (e)
                {
                    case (byte)'"': codePoint = '"'; i += 2; break;
                    case (byte)'\\': codePoint = '\\'; i += 2; break;
                    case (byte)'/': codePoint = '/'; i += 2; break;
                    case (byte)'b': codePoint = '\b'; i += 2; break;
                    case (byte)'f': codePoint = '\f'; i += 2; break;
                    case (byte)'n': codePoint = '\n'; i += 2; break;
                    case (byte)'r': codePoint = '\r'; i += 2; break;
                    case (byte)'t': codePoint = '\t'; i += 2; break;
                    default:
                        int unit = ReadHex4(source, i + 2, end);
                        if (unit >= 0xD800 && unit <= 0xDBFF)
                        {
                            int low = ReadHex4(source, i + 8, end);
                            codePoint = 0x10000 + ((unit - 0xD800) << 10) + (low - 0xDC00);
                            i += 12;
                        }
                        else
                        {
                            codePoint = unit;
                            i += 6;
                        }
                        break;
                }
                int needed = codePoint < 0x80 ? 1 : codePoint < 0x800 ? 2 : codePoint < 0x10000 ? 3 : 4;
                if (written + needed > destination.Length) return -1;
                switch (needed)
                {
                    case 1:
                        destination[written++] = (byte)codePoint;
                        break;
                    case 2:
                        destination[written++] = (byte)(0xC0 | (codePoint >> 6));
                        destination[written++] = (byte)(0x80 | (codePoint & 0x3F));
                        break;
                    case 3:
                        destination[written++] = (byte)(0xE0 | (codePoint >> 12));
                        destination[written++] = (byte)(0x80 | ((codePoint >> 6) & 0x3F));
                        destination[written++] = (byte)(0x80 | (codePoint & 0x3F));
                        break;
                    default:
                        destination[written++] = (byte)(0xF0 | (codePoint >> 18));
                        destination[written++] = (byte)(0x80 | ((codePoint >> 12) & 0x3F));
                        destination[written++] = (byte)(0x80 | ((codePoint >> 6) & 0x3F));
                        destination[written++] = (byte)(0x80 | (codePoint & 0x3F));
                        break;
                }
            }
            return written;
        }

        private bool ScanNumber()
        {
            int start = _pos;
            int p = _pos;
            if (Buffer[p] == (byte)'-') p++;
            if (p >= _end) return Fail("invalid number");
            if (Buffer[p] == (byte)'0')
            {
                p++;
            }
            else if (Buffer[p] >= (byte)'1' && Buffer[p] <= (byte)'9')
            {
                while (p < _end && IsDigit(Buffer[p])) p++;
            }
            else
            {
                return Fail("invalid number");
            }
            if (p < _end && Buffer[p] == (byte)'.')
            {
                p++;
                if (p >= _end || !IsDigit(Buffer[p])) return Fail("invalid number");
                while (p < _end && IsDigit(Buffer[p])) p++;
            }
            if (p < _end && (Buffer[p] == (byte)'e' || Buffer[p] == (byte)'E'))
            {
                p++;
                if (p < _end && (Buffer[p] == (byte)'+' || Buffer[p] == (byte)'-')) p++;
                if (p >= _end || !IsDigit(Buffer[p])) return Fail("invalid number");
                while (p < _end && IsDigit(Buffer[p])) p++;
            }
            if (p < _end && !IsDelimiter(Buffer[p])) return Fail("invalid number");
            if (p - start > _limits.MaxNumberChars)
            {
                return FailLimit("JSON number at byte " + BasisGlbErrors.N(start - _start) + " is longer than "
                    + BasisGlbErrors.N(_limits.MaxNumberChars) + " characters.");
            }
            ValueStart = start;
            ValueLength = p - start;
            ValueHasEscapes = false;
            _pos = p;
            return true;
        }

        private bool ScanLiteral(string literal)
        {
            if (_pos + literal.Length > _end) return Fail("expected a value");
            for (int i = 0; i < literal.Length; i++)
            {
                if (Buffer[_pos + i] != literal[i]) return Fail("expected a value");
            }
            int after = _pos + literal.Length;
            if (after < _end && !IsDelimiter(Buffer[after])) return Fail("expected a value");
            ValueStart = _pos;
            ValueLength = literal.Length;
            ValueHasEscapes = false;
            _pos = after;
            return true;
        }

        private static bool IsDigit(byte c)
        {
            return c >= (byte)'0' && c <= (byte)'9';
        }

        private static bool IsDelimiter(byte c)
        {
            return c == (byte)',' || c == (byte)']' || c == (byte)'}' || c == (byte)' ' || c == (byte)'\n'
                || c == (byte)'\r' || c == (byte)'\t' || c == 0;
        }

        private bool CountToken()
        {
            TokenCount++;
            if (TokenCount > _limits.MaxTokens)
            {
                return FailLimit("JSON has more than " + BasisGlbErrors.N(_limits.MaxTokens) + " tokens.");
            }
            return true;
        }

        private bool Fail(string what)
        {
            ErrorOffset = _pos - _start;
            Error = "JSON syntax error at byte " + BasisGlbErrors.N(ErrorOffset) + ": " + what + ".";
            return false;
        }

        private bool FailPlain(string message)
        {
            ErrorOffset = _pos - _start;
            Error = message;
            return false;
        }

        private bool FailLimit(string message)
        {
            ErrorIsLimit = true;
            return FailPlain(message);
        }
    }
}
