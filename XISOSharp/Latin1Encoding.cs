using System.Text;

namespace XISOSharp;

/// <summary>
/// ISO-8859-1 (Latin-1) encoding that maps byte values 0–255 directly to
/// Unicode code points U+0000–U+00FF. Used for XISO filenames which may
/// contain extended byte values (e.g. Japanese or accented characters).
/// </summary>
internal static class Latin1Encoding
{
    /// <summary>Shared singleton instance.</summary>
    internal static readonly Encoding Instance = new Latin1EncodingInternal();

    /// <summary>
    /// Internal <see cref="Encoding"/> implementation that maps bytes 0–255
    /// directly to Unicode code points U+0000–U+00FF.
    /// </summary>
    private sealed class Latin1EncodingInternal : Encoding
    {
        // BUG-LIB-039: Latin-1 is a strict 1 char = 1 byte mapping, so every
        // counting/encoding entry point must agree — and every decoding entry
        // point must map bytes straight through. The base Encoding span and
        // string overloads do not all delegate to the array overloads below,
        // so each is overridden explicitly instead of relying on delegation.

        /// <inheritdoc/>
        public override int GetByteCount(char[] chars, int index, int count)
        {
            ArgumentNullException.ThrowIfNull(chars);
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (index + count > chars.Length)
                throw new ArgumentOutOfRangeException(nameof(count));
            ValidateEncodable(chars.AsSpan(index, count), nameof(chars));
            return count;
        }

        /// <inheritdoc/>
        public override int GetByteCount(string s)
        {
            ArgumentNullException.ThrowIfNull(s);
            ValidateEncodable(s.AsSpan(), nameof(s));
            return s.Length;
        }

        /// <inheritdoc/>
        public override int GetByteCount(ReadOnlySpan<char> chars)
        {
            ValidateEncodable(chars, nameof(chars));
            return chars.Length;
        }

        /// <inheritdoc/>
        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex)
        {
            ArgumentNullException.ThrowIfNull(chars);
            ArgumentNullException.ThrowIfNull(bytes);
            for (var i = 0; i < charCount; i++)
            {
                var c = chars[charIndex + i];
                if (c > 0xFF)
                {
                    throw new ArgumentException(
                        $"Character U+{(int)c:X4} at position {charIndex + i} is outside the Latin-1 range (0x00–0xFF).",
                        nameof(chars));
                }

                bytes[byteIndex + i] = (byte)c;
            }

            return charCount;
        }

        /// <inheritdoc/>
        public override int GetBytes(string s, int charIndex, int charCount, byte[] bytes, int byteIndex)
        {
            ArgumentNullException.ThrowIfNull(s);
            ArgumentNullException.ThrowIfNull(bytes);
            for (var i = 0; i < charCount; i++)
            {
                var c = s[charIndex + i];
                if (c > 0xFF)
                {
                    throw new ArgumentException(
                        $"Character U+{(int)c:X4} at position {charIndex + i} is outside the Latin-1 range (0x00–0xFF).",
                        nameof(s));
                }

                bytes[byteIndex + i] = (byte)c;
            }

            return charCount;
        }

        /// <inheritdoc/>
        public override int GetBytes(ReadOnlySpan<char> chars, Span<byte> bytes)
        {
            if (bytes.Length < chars.Length)
            {
                throw new ArgumentException(
                    "Destination is too small for the encoded bytes.", nameof(bytes));
            }

            for (var i = 0; i < chars.Length; i++)
            {
                var c = chars[i];
                if (c > 0xFF)
                {
                    throw new ArgumentException(
                        $"Character U+{(int)c:X4} at position {i} is outside the Latin-1 range (0x00–0xFF).",
                        nameof(chars));
                }

                bytes[i] = (byte)c;
            }

            return chars.Length;
        }

        /// <inheritdoc/>
        public override int GetCharCount(byte[] bytes, int index, int count) => count;

        /// <inheritdoc/>
        public override int GetCharCount(ReadOnlySpan<byte> bytes) => bytes.Length;

        /// <inheritdoc/>
        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
        {
            for (var i = 0; i < byteCount; i++)
            {
                chars[charIndex + i] = (char)bytes[byteIndex + i];
            }

            return byteCount;
        }

        /// <inheritdoc/>
        public override int GetChars(ReadOnlySpan<byte> bytes, Span<char> chars)
        {
            if (chars.Length < bytes.Length)
            {
                throw new ArgumentException(
                    "Destination is too small for the decoded characters.", nameof(chars));
            }

            for (var i = 0; i < bytes.Length; i++)
            {
                chars[i] = (char)bytes[i];
            }

            return bytes.Length;
        }

        private static void ValidateEncodable(ReadOnlySpan<char> chars, string paramName)
        {
            for (var i = 0; i < chars.Length; i++)
            {
                if (chars[i] > 0xFF)
                {
                    throw new ArgumentException(
                        $"Character U+{(int)chars[i]:X4} at position {i} is outside the Latin-1 range (0x00–0xFF).",
                        paramName);
                }
            }
        }

        /// <inheritdoc/>
        public override int GetMaxByteCount(int charCount) => charCount;

        /// <inheritdoc/>
        public override int GetMaxCharCount(int byteCount) => byteCount;
    }
}
