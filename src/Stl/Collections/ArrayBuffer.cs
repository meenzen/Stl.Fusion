using System.Buffers;

namespace Stl.Collections;

// List-like struct that typically requires zero allocations
// (it relies on ArrayPool<T>.Shared & disposes its buffer);
// it is supposed to be used as a temp. buffer in various
// enumeration scenarios.
// ArrayBuffer<T> vs MemoryBuffer<T>: they are almost identical, but
// ArrayBuffer isn't a ref struct, so you can store it in fields.
public struct ArrayBuffer<T>
{
    public const int MinCapacity = 8;
    public const int MaxCapacity = 1 << 30;
    private static readonly ArrayPool<T> Pool = ArrayPool<T>.Shared;

    private int _count;

    public T[] Buffer { get; private set; }
    public Span<T> Span => Buffer.AsSpan(0, Count);
    public int Capacity => Buffer.Length;
    public bool MustClean { get; }
    public int Count {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _count;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set {
            if (value < 0 || value > Capacity)
                throw new ArgumentOutOfRangeException(nameof(value));
            _count = value;
        }
    }

    public T this[int index] {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
#pragma warning disable MA0012
        get => index < Count ? Buffer[index] : throw new IndexOutOfRangeException();
#pragma warning restore MA0012
    }

    private ArrayBuffer(bool mustClean, int capacity)
    {
        MustClean = mustClean;
        capacity = ComputeCapacity(capacity, MinCapacity);
        Buffer = Pool.Rent(capacity);
        _count = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ArrayBuffer<T> Lease(bool mustClean, int capacity = MinCapacity)
        => new(mustClean, capacity);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ArrayBuffer<T> LeaseAndSetCount(bool mustClean, int count)
        => new(mustClean, count) { Count = count };

    public void Release()
    {
        // ReSharper disable once ConditionIsAlwaysTrueOrFalse
        if (Buffer != null)
            Pool.Return(Buffer, MustClean);
        Buffer = null!;
    }

    public Span<T>.Enumerator GetEnumerator() => Span.GetEnumerator();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T[] ToArray() => Span.ToArray();

    public List<T> ToList()
    {
        var list = new List<T>(Count);
        foreach (var item in Span)
            list.Add(item);
        return list;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetItem(int index, T item)
    {
        if (index >= Count)
#pragma warning disable MA0012
            throw new IndexOutOfRangeException();
#pragma warning restore MA0012
        Buffer[index] = item;
    }

    public void AddRange(IEnumerable<T> items)
    {
        foreach (var item in items)
            Add(item);
    }

    public void AddRange(IReadOnlyCollection<T> items)
    {
        EnsureCapacity(Count + items.Count);
        foreach (var item in items)
            Add(item);
    }

    public void AddSpan(ReadOnlySpan<T> span)
    {
        EnsureCapacity(_count + span.Length);
        span.CopyTo(Buffer.AsSpan(_count));
        _count += span.Length;
    }

    public void Add(T item)
    {
        if (Count >= Capacity)
            EnsureCapacity(Count + 1);
        Buffer[Count++] = item;
    }

    public void Insert(int index, T item)
    {
        if (Count >= Capacity)
            EnsureCapacity(Count + 1);
        var copyLength = Count - index;
        if (copyLength < 0)
            throw new ArgumentOutOfRangeException(nameof(index));
        var span = Buffer.AsSpan(0, ++Count);
        var source = span.Slice(index, copyLength);
        var target = span[(index + 1)..];
        source.CopyTo(target);
        span[index] = item;
    }

    public void RemoveAt(int index)
    {
        var copyLength = Count - index - 1;
        if (copyLength < 0)
            throw new ArgumentOutOfRangeException(nameof(index));
        var span = Buffer.AsSpan(0, Count--);
        var source = span.Slice(index + 1, copyLength);
        var target = span[index..];
        source.CopyTo(target);
    }

    public void Clear()
    {
        ChangeLease(Pool.Rent(MinCapacity));
        Count = 0;
    }

    public void CopyTo(T[] array, int arrayIndex)
        => Buffer.CopyTo(array.AsSpan(arrayIndex));

    public void EnsureCapacity(int capacity)
    {
        capacity = ComputeCapacity(capacity, Capacity);
        var newLease = Pool.Rent(capacity);
        Span.CopyTo(newLease.AsSpan());
        ChangeLease(newLease);
    }

    // Private methods

    private static int ComputeCapacity(int requestedCapacity, int minCapacity)
    {
        if (requestedCapacity < minCapacity)
            requestedCapacity = minCapacity;
        else if (requestedCapacity > MaxCapacity)
            throw new ArgumentOutOfRangeException(nameof(requestedCapacity));
        return (int) Bits.GreaterOrEqualPowerOf2((uint) requestedCapacity);
    }

    private void ChangeLease(T[] newLease)
    {
        Pool.Return(Buffer, MustClean);
        Buffer = newLease;
    }
}
