namespace AgentKick75.Core.Lighting;

/// <summary>
/// The complete raw eight-byte Kick75 side-light state. The constructor clones
/// input so a captured baseline cannot be modified by its caller.
/// </summary>
public sealed class SideLightState : IEquatable<SideLightState>
{
    public const int Length = 8;

    private readonly byte[] bytes;

    public SideLightState(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Length)
        {
            throw new ArgumentException($"Side-light state must contain exactly {Length} bytes.", nameof(bytes));
        }

        this.bytes = bytes.ToArray();
    }

    public ReadOnlyMemory<byte> Bytes => bytes;

    public static bool TryCreate(ReadOnlySpan<byte> bytes, out SideLightState? state)
    {
        if (bytes.Length != Length)
        {
            state = null;
            return false;
        }

        state = new SideLightState(bytes);
        return true;
    }

    public byte[] ToArray()
    {
        return bytes.ToArray();
    }

    public void CopyTo(Span<byte> destination)
    {
        if (destination.Length < Length)
        {
            throw new ArgumentException($"Destination must have room for {Length} bytes.", nameof(destination));
        }

        bytes.CopyTo(destination);
    }

    public bool Equals(SideLightState? other)
    {
        return other is not null && bytes.AsSpan().SequenceEqual(other.bytes);
    }

    public override bool Equals(object? obj)
    {
        return obj is SideLightState other && Equals(other);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (byte value in bytes)
        {
            hash.Add(value);
        }

        return hash.ToHashCode();
    }

    public override string ToString()
    {
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
