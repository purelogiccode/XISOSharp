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
        /// <inheritdoc/>
        public override int GetByteCount(char[] chars, int index, int count)
        {
            return count;
        }

        /// <inheritdoc/>
        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex)
        {
            for (var i = 0; i < charCount; i++)
            {
                var c = chars[charIndex + i];
                if (c > 0xFF)
                    throw new ArgumentException(
                        $"Character U+{(int)c:X4} at position {charIndex + i} is outside the Latin-1 range (0x00–0xFF).",
                        nameof(chars));

                bytes[byteIndex + i] = (byte)c;
            }

            return charCount;
        }

        /// <inheritdoc/>
        public override int GetCharCount(byte[] bytes, int index, int count)
        {
            return count;
        }

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
        public override int GetMaxByteCount(int charCount) => charCount;

        /// <inheritdoc/>
        public override int GetMaxCharCount(int byteCount) => byteCount;
    }
}