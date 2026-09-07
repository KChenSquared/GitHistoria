using System.Text;

namespace Historia.Services.Graph;

/// <summary>
/// Explicit FNV-1a hash over UTF-8 bytes (§7.5). Used for lane color seeding — <b>not</b>
/// <see cref="string.GetHashCode()"/>, which is randomized per process in .NET and would give
/// a branch a different color every launch.
/// </summary>
public static class StableHash
{
    private const uint FnvOffsetBasis = 2166136261;
    private const uint FnvPrime = 16777619;

    public const ulong Fnv64OffsetBasis = 14695981039346656037;
    public const ulong Fnv64Prime = 1099511628211;

    public static uint Fnv1a(string text)
    {
        var hash = FnvOffsetBasis;
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            hash ^= b;
            hash *= FnvPrime;
        }

        return hash;
    }

    /// <summary>
    /// One 64-bit FNV-1a step: folds <paramref name="text"/> (as UTF-16 code units) plus a
    /// record separator into <paramref name="hash"/>. Chain calls to build a stable
    /// (cross-process) fingerprint over an ordered sequence of strings.
    /// </summary>
    public static ulong Fnv1a64Step(ulong hash, ReadOnlySpan<char> text)
    {
        unchecked
        {
            foreach (var c in text)
            {
                hash ^= c;
                hash *= Fnv64Prime;
            }

            hash ^= (byte)'\n';
            hash *= Fnv64Prime;
        }

        return hash;
    }
}
