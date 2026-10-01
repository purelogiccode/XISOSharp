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
            ValidateRange(index, count, chars.Length, nameof(index), nameof(count));
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
            ValidateRange(charIndex, charCount, chars.Length, nameof(charIndex), nameof(charCount));
            ValidateDestination(byteIndex, charCount, bytes.Length, nameof(byteIndex), nameof(bytes),
                "Destination is too small for the encoded bytes.");

            for (int i = 0; i < charCount; i++)
            {
                char c = chars[charIndex + i];
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
            ValidateRange(charIndex, charCount, s.Length, nameof(charIndex), nameof(charCount));
            ValidateDestination(byteIndex, charCount, bytes.Length, nameof(byteIndex), nameof(bytes),
                "Destination is too small for the encoded bytes.");

            for (int i = 0; i < charCount; i++)
            {
                char c = s[charIndex + i];
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

            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
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
        public override int GetCharCount(byte[] bytes, int index, int count)
        {
            ArgumentNullException.ThrowIfNull(bytes);
            ValidateRange(index, count, bytes.Length, nameof(index), nameof(count));
            return count;
        }

        /// <inheritdoc/>
        public override int GetCharCount(ReadOnlySpan<byte> bytes) => bytes.Length;

        /// <inheritdoc/>
        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
        {
            ArgumentNullException.ThrowIfNull(bytes);
            ArgumentNullException.ThrowIfNull(chars);
            ValidateRange(byteIndex, byteCount, bytes.Length, nameof(byteIndex), nameof(byteCount));
            ValidateDestination(charIndex, byteCount, chars.Length, nameof(charIndex), nameof(chars),
                "Destination is too small for the decoded characters.");

            for (int i = 0; i < byteCount; i++)
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

            for (int i = 0; i < bytes.Length; i++)
            {
                chars[i] = (char)bytes[i];
            }

            return bytes.Length;
        }

        /// <summary>
        /// Validates an <c>(index, count)</c> pair against a source length,
        /// surfacing the <see cref="Encoding"/> contract's
        /// <see cref="ArgumentOutOfRangeException"/> instead of letting the
        /// loops throw <see cref="IndexOutOfRangeException"/> (Todo #62).
        /// </summary>
        private static void ValidateRange(int index, int count, int length, string indexName, string countName)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index, indexName);
            ArgumentOutOfRangeException.ThrowIfNegative(count, countName);
            // Subtraction, not `index + count`, so int overflow cannot slip a
            // huge count past the check.
            if (index > length || count > length - index)
            {
                throw new ArgumentOutOfRangeException(countName);
            }
        }

        /// <summary>
        /// Validates a destination <c>(index, count)</c> pair without integer
        /// overflow, surfacing <see cref="ArgumentOutOfRangeException"/> for an
        /// out-of-range index and <see cref="ArgumentException"/> for a
        /// too-small destination, matching the <see cref="Encoding"/> contract.
        /// </summary>
        private static void ValidateDestination(int index, int count, int length, string indexName,
            string destinationName, string message)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index, indexName);
            if (index > length)
            {
                throw new ArgumentOutOfRangeException(indexName);
            }

            if (count > length - index)
            {
                throw new ArgumentException(message, destinationName);
            }
        }

        private static void ValidateEncodable(ReadOnlySpan<char> chars, string paramName)
        {
            for (int i = 0; i < chars.Length; i++)
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
