namespace XISOSharp;

/// <summary>
/// Boyer-Moore pattern search for arbitrary byte patterns.
/// Used during XISO creation to find and patch the media-enable signature
/// in <c>.xbe</c> executable files.
/// Ported from extract-xiso.c 2.7.1.
/// </summary>
public class BoyerMoore
{
    private readonly byte[] _pattern;
    private readonly int _patLen;
    private int[]? _bcTable;
    private int[]? _gsTable;
    private readonly int _alphabetSize;

    /// <summary>
    /// Initializes a new Boyer-Moore pattern matcher.
    /// The pattern is stored but tables are not built until <see cref="Init"/> is called.
    /// </summary>
    /// <param name="pattern">Byte pattern to search for.</param>
    /// <param name="alphabetSize">Size of the alphabet for the bad-character table (default 256).</param>
    public BoyerMoore(byte[] pattern, int alphabetSize = Constants.DefaultAlphabetSize)
    {
        _pattern = pattern;
        _patLen = pattern.Length;
        _alphabetSize = alphabetSize;
    }

    /// <summary>
    /// Builds the bad-character and good-suffix shift tables.
    /// Must be called before <see cref="Search(byte[])"/> or
    /// <see cref="Search(byte[], int, int)"/>.
    /// Safe to call multiple times; each call rebuilds the tables.
    /// </summary>
    public void Init()
    {
        int i;

        _bcTable = new int[_alphabetSize];
        for (i = 0; i < _alphabetSize; i++)
        {
            _bcTable[i] = _patLen;
        }

        for (i = 0; i < _patLen - 1; i++)
        {
            _bcTable[_pattern[i]] = _patLen - i - 1;
        }

        _gsTable = new int[2 * (_patLen + 1) + 2];

        for (i = 1; i <= _patLen; i++)
        {
            _gsTable[i] = 2 * _patLen - i;
        }

        i = _patLen;
        var j = _patLen + 1;
        while (i > 0)
        {
            _gsTable[_patLen + 1 + i] = j;

            while (j <= _patLen && _pattern[i - 1] != _pattern[j - 1])
            {
                if (_gsTable[j] > _patLen - i)
                {
                    _gsTable[j] = _patLen - i;
                }

                j = _gsTable[_patLen + 1 + j];
            }

            i--;
            j--;
        }

        for (i = 1; i <= j; i++)
            if (_gsTable[i] > _patLen + j - i)
            {
                _gsTable[i] = _patLen + j - i;
            }

        var k = _gsTable[_patLen + 1 + j];

        while (j <= _patLen)
        {
            while (j <= k)
            {
                if (_gsTable[j] >= k - j + _patLen)
                {
                    _gsTable[j] = k - j + _patLen;
                }

                j++;
            }
            k = _gsTable[_patLen + 1 + k];
        }
    }

    /// <summary>
    /// Searches for the pattern within a subrange of the given text buffer.
    /// </summary>
    /// <param name="text">Byte array to search in.</param>
    /// <param name="startIndex">Index in <paramref name="text"/> to start the search.</param>
    /// <param name="length">Number of bytes to consider from <paramref name="startIndex"/>.</param>
    /// <returns>
    /// The index of the first match relative to the start of <paramref name="text"/>,
    /// or -1 if the pattern was not found.
    /// </returns>
    public int Search(byte[] text, int startIndex, int length)
    {
        int j;

        var i = j = _patLen - 1;

        while (j < length && i >= 0)
        {
            if (text[startIndex + j] == _pattern[i])
            {
                i--;
                j--;
            }
            else
            {
                var k = _gsTable![i + 1];
                var l = _bcTable![text[startIndex + j]];

                j += Math.Max(k, l);
                i = _patLen - 1;
            }
        }

        return i < 0 ? startIndex + j + 1 : -1;
    }

    /// <summary>
    /// Searches for the pattern in the entire text buffer starting at offset 0.
    /// </summary>
    /// <param name="text">Byte array to search in.</param>
    /// <returns>
    /// The index of the first match, or -1 if the pattern was not found.
    /// </returns>
    public int Search(byte[] text)
    {
        return Search(text, 0, text.Length);
    }

    /// <summary>
    /// Releases the shift tables. The instance must be re-initialized with
    /// <see cref="Init"/> before searching again.
    /// </summary>
    public void Done()
    {
        _bcTable = null;
        _gsTable = null;
    }
}
