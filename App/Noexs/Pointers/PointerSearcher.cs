// Pointer search algorithm ported from "PointerSearcher" by Takumi4685
// https://github.com/Takumi4685/PointerSearcher (v0.04)
//
// MIT License
//
// Copyright (c) 2019 Takumi4685
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System.Runtime.InteropServices;
using System.Text;
using Noexs.Dump;

namespace Noexs.Pointers;

public enum RegionType
{
    MAIN,
    HEAP,
}

/// <summary>An address relative to the start of main or heap.</summary>
public readonly record struct Address(RegionType Type, long Offset) : IComparable<Address>
{
    public long OffsetFrom(Address src)
    {
        return src.Type != Type ? long.MaxValue : Offset - src.Offset;
    }

    public int CompareTo(Address obj)
    {
        if (Type != obj.Type)
        {
            return Type == RegionType.MAIN ? -1 : 1;
        }
        return Offset.CompareTo(obj.Offset);
    }
}

/// <summary>A heap address together with every location that holds a pointer to it.</summary>
public sealed class PointedAddress(Address addr)
{
    public Address Addr { get; } = addr;

    public List<Address> PointedFrom { get; } = new();
}

public sealed class PointerInfo
{
    private Dictionary<Address, PointedAddress>? tmpDic = new();

    public List<PointedAddress> PointedList { get; private set; } = new();

    public void AddPointer(Address from, Address to)
    {
        if (!tmpDic!.TryGetValue(to, out PointedAddress? pa))
        {
            pa = new PointedAddress(to);
            tmpDic.Add(to, pa);
        }
        pa.PointedFrom.Add(from);
    }

    /// <returns>index of the highest pointed address &lt;= target, or -1</returns>
    public int FindNearest(Address target)
    {
        if (PointedList.Count == 0)
        {
            return -1;
        }
        int l = 0;
        int r = PointedList.Count - 1;
        while (l <= r)
        {
            int m = l + (r - l) / 2;
            int result = target.CompareTo(PointedList[m].Addr);
            if (result == 0)
            {
                return m;
            }
            if (result < 0)
            {
                r = m - 1;
            }
            else
            {
                l = m + 1;
            }
        }
        return r;
    }

    public void MakeList()
    {
        PointedList = new List<PointedAddress>(tmpDic!.Values);
        tmpDic = null;
        PointedList.Sort((a, b) => a.Addr.CompareTo(b.Addr));
    }
}

/// <summary>One step of a pointer path, stored from the target back to main.</summary>
public abstract class PathElement
{
    public abstract string ToString(string org);

    public abstract long ParseAddress(long currentAddress, long currentData);
}

public sealed class OffsetElement(long offset) : PathElement
{
    public long Offset { get; } = offset;

    // at least 2 digits (PointerSearcher v0.04: avoids a Noexes parse exception)
    public override string ToString(string org)
    {
        return org + "+" + Offset.ToString("X2");
    }

    public override long ParseAddress(long currentAddress, long currentData)
    {
        return currentAddress + Offset;
    }
}

public sealed class PointerJumpElement : PathElement
{
    public static readonly PointerJumpElement Instance = new();

    public override string ToString(string org)
    {
        return "[" + org + "]";
    }

    public override long ParseAddress(long currentAddress, long currentData)
    {
        return currentData;
    }
}

/// <summary>A found pointer chain, e.g. <c>[[main+1A2B0]+10]+08</c>.</summary>
public sealed class PointerPath(PathElement[] elements)
{
    public IReadOnlyList<PathElement> Elements { get; } = elements;

    public override string ToString()
    {
        string str = "main";
        for (int i = Elements.Count - 1; i >= 0; i--)
        {
            str = Elements[i].ToString(str);
        }
        return str;
    }
}

/// <summary>Reads pointers out of a Noexes .dmp file (NoexsDumpDataReader).</summary>
public sealed class DumpDataReader : IDisposable
{
    private readonly MemoryDump dump;
    private readonly Dictionary<long, long> readData = new(); // key:address, value:data

    public DumpDataReader(string path, long mainStart, long mainEnd, long heapStart, long heapEnd)
    {
        dump = new MemoryDump(path, readOnly: true);
        MainStart = mainStart;
        MainEnd = mainEnd;
        HeapStart = heapStart;
        HeapEnd = heapEnd;
    }

    public long MainStart { get; }
    public long MainEnd { get; }
    public long HeapStart { get; }
    public long HeapEnd { get; }

    /// <summary>Collects every 8-byte value in main/heap that points into the heap.</summary>
    public PointerInfo Read(CancellationToken token, Action<double> progress)
    {
        var pointerInfo = new PointerInfo();
        IReadOnlyList<DumpIndex> indices = dump.Indices;
        for (int index = 0; index < indices.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            DumpIndex x = indices[index];
            if (!IsMainHeapAddress(x.Address))
            {
                continue;
            }
            RegionType type = GetMemoryType(x.Address);
            long address = x.Address - GetStartAddress(type);
            ReadOnlySpan<long> values = MemoryMarshal.Cast<byte, long>(dump.GetBuffer(x).Span);
            for (int i = 0; i < values.Length; i++)
            {
                if ((i & 0xFFFF) == 0)
                {
                    token.ThrowIfCancellationRequested();
                }
                long data = values[i];
                if (IsHeapAddress(data))
                {
                    pointerInfo.AddPointer(new Address(type, address), new Address(RegionType.HEAP, data - HeapStart));
                }
                address += 8;
            }
            progress((index + 1) / (double)indices.Count);
        }
        pointerInfo.MakeList();
        return pointerInfo;
    }

    public long ReadInt64(long address)
    {
        DumpIndex? idx = dump.GetIndex(address);
        if (idx == null || address + 8 > idx.EndAddress)
        {
            return 0;
        }
        MappedBuffer buffer = dump.GetBuffer(idx);
        buffer.Position = (int)(address - idx.Address);
        return buffer.GetLong();
    }

    /// <summary>Follows <paramref name="path"/> from main; 0 if it leaves main/heap.</summary>
    public long TryToParseAbs(IReadOnlyList<PathElement> path)
    {
        long address = MainStart;
        for (int i = path.Count - 1; i >= 0; i--)
        {
            if (!readData.TryGetValue(address, out long data))
            {
                data = ReadInt64(address);
                readData.Add(address, data);
            }
            address = path[i].ParseAddress(address, data);
            if (address == 0 || !IsMainHeapAddress(address))
            {
                return 0;
            }
        }
        return address;
    }

    public bool IsHeap(long address)
    {
        return IsHeapAddress(address);
    }

    private bool IsMainHeapAddress(long a)
    {
        return (MainStart <= a && a < MainEnd) || (HeapStart <= a && a < HeapEnd);
    }

    private bool IsHeapAddress(long a)
    {
        return HeapStart <= a && a < HeapEnd;
    }

    private RegionType GetMemoryType(long address)
    {
        return MainStart <= address && address < MainEnd ? RegionType.MAIN : RegionType.HEAP;
    }

    private long GetStartAddress(RegionType type)
    {
        return type == RegionType.MAIN ? MainStart : HeapStart;
    }

    public void Dispose()
    {
        dump.Dispose();
    }
}

public sealed class FindPath(int maxOffsetNum, long maxOffsetAddress)
{
    /// <summary>Stops a search that would otherwise run out of memory.</summary>
    public const int MaxResults = 1_000_000;

    public bool LimitReached { get; private set; }

    /// <param name="progress">receives progress increments (in percent)</param>
    public void Search(CancellationToken token, Action<double> progress, double progAddValue, PointerInfo info,
        int depth, List<PathElement> path, Address current, List<PointerPath> result)
    {
        int nearestIndex = info.FindNearest(current);

        const double reportMin = 0.02;
        double progAddEach = progAddValue / maxOffsetNum;
        if (progAddEach < reportMin)
        {
            progAddEach = 0;
        }

        for (int i = nearestIndex; i > nearestIndex - maxOffsetNum; i--)
        {
            token.ThrowIfCancellationRequested();
            if (i < 0 || LimitReached)
            {
                if (progAddEach != 0) progress(progAddEach);
                continue;
            }
            PointedAddress pointed = info.PointedList[i];
            long offset = current.OffsetFrom(pointed.Addr);
            if (offset >= maxOffsetAddress)
            {
                if (progAddEach != 0) progress(progAddEach);
                continue;
            }
            if (offset > 0)
            {
                path.Add(new OffsetElement(offset));
            }

            double progAddNest = progAddEach / pointed.PointedFrom.Count;
            if (progAddNest < reportMin)
            {
                progAddNest = 0;
            }

            foreach (Address next in pointed.PointedFrom)
            {
                token.ThrowIfCancellationRequested();
                path.Add(PointerJumpElement.Instance);

                if (next.Type == RegionType.MAIN)
                {
                    path.Add(new OffsetElement(next.Offset));
                    result.Add(new PointerPath(path.ToArray()));
                    path.RemoveAt(path.Count - 1);
                    if (result.Count >= MaxResults)
                    {
                        LimitReached = true;
                    }
                    if (progAddNest != 0) progress(progAddNest);
                }
                else if (depth > 1 && !LimitReached)
                {
                    Search(token, progress, progAddNest, info, depth - 1, path, next, result);
                }
                else if (progAddNest != 0)
                {
                    progress(progAddNest);
                }
                path.RemoveAt(path.Count - 1);
            }
            if (progAddEach != 0 && progAddNest == 0) progress(progAddEach);
            if (offset > 0)
            {
                path.RemoveAt(path.Count - 1);
            }
        }
        if (progAddEach == 0 && progAddValue != 0) progress(progAddValue);
    }

    /// <summary>
    /// Keeps the paths that resolve to the given target in every extra dump. A target of 0 only
    /// requires the path to end inside the heap.
    /// </summary>
    public static List<PointerPath> NarrowDown(CancellationToken token, Action<double> progress,
        List<PointerPath> list, IReadOnlyList<(DumpDataReader Reader, long Target)> dumps)
    {
        var result = new List<PointerPath>(list.Count);
        for (int i = 0; i < list.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            PointerPath path = list[i];
            bool keep = true;
            foreach ((DumpDataReader dump, long target) in dumps)
            {
                long parsed = dump.TryToParseAbs(path.Elements);
                if (target == 0 ? !dump.IsHeap(parsed) : parsed != target)
                {
                    keep = false;
                    break;
                }
            }
            if (keep)
            {
                result.Add(path);
            }
            if ((i & 0x3FF) == 0)
            {
                progress((i + 1) / (double)list.Count);
            }
        }
        progress(1);
        return result;
    }
}
