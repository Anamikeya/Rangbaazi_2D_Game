namespace Rangbaazi.Sim;

/// <summary>
/// FNV-1a 64-bit hasher for game state. Peers exchange these checksums to detect desyncs;
/// the result is stable across processes and machines (unlike System.HashCode).
/// </summary>
public struct StateHasher
{
    private const ulong OffsetBasis = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    private ulong _hash;
    private bool _started;

    public readonly ulong Value => _started ? _hash : OffsetBasis;

    public void Add(ulong value)
    {
        if (!_started)
        {
            _hash = OffsetBasis;
            _started = true;
        }
        for (int i = 0; i < 8; i++)
        {
            _hash ^= (byte)(value >> (i * 8));
            _hash *= Prime;
        }
    }

    public void Add(long value) => Add((ulong)value);
    public void Add(int value) => Add((ulong)(uint)value);
    public void Add(bool value) => Add(value ? 1UL : 0UL);
}
