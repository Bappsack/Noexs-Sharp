namespace Noexs.Core;

/// <summary>
/// Finds the main executable and heap ranges in a process memory map, using the same module
/// naming as the Tools tab (one R-X CODE_STATIC module: main; several: rtld, main, sdk, ...).
/// </summary>
public static class MemoryRegions
{
    public readonly record struct Ranges(long? MainStart, long? MainEnd, long? HeapStart, long? HeapEnd);

    public static Ranges Detect(IEnumerable<MemoryInfo> memoryInfos)
    {
        var infos = memoryInfos.Where(i => i.Type != MemoryType.UNMAPPED).OrderBy(i => i.Address).ToList();

        long? mainStart = null, mainEnd = null;
        var modules = infos.Select((info, index) => (info, index))
            .Where(x => IsModuleText(x.info))
            .ToList();
        if (modules.Count > 0)
        {
            int k = modules.Count == 1 ? modules[0].index : modules[1].index;
            mainStart = infos[k].Address;
            long end = infos[k].NextAddress;
            // the module continues with its .rodata/.data/.bss mappings up to the next module's code
            for (int j = k + 1; j < infos.Count; j++)
            {
                MemoryInfo next = infos[j];
                if (next.Address != end || IsModuleText(next)
                    || next.Type is not (MemoryType.CODE_STATIC or MemoryType.CODE_MUTABLE))
                {
                    break;
                }
                end = next.NextAddress;
            }
            mainEnd = end;
        }

        long? heapStart = null, heapEnd = null;
        var heap = infos.Where(i => i.Type == MemoryType.HEAP).ToList();
        if (heap.Count > 0)
        {
            heapStart = heap[0].Address;
            heapEnd = heap[^1].NextAddress;
        }
        return new Ranges(mainStart, mainEnd, heapStart, heapEnd);
    }

    /// <summary>A loaded executable: its code (R-X) mapping and the whole module including data.</summary>
    public sealed record Module(string Name, long Start, long TextEnd, long End)
    {
        public bool Contains(long address) => address >= Start && address < End;
    }

    /// <summary>Modules named like the Tools tab (rtld, main, sdk, subsdkN; "main" when there is only one).</summary>
    public static List<Module> Modules(IEnumerable<MemoryInfo> memoryInfos)
    {
        var infos = memoryInfos.Where(i => i.Type != MemoryType.UNMAPPED).OrderBy(i => i.Address).ToList();
        var texts = infos.Select((info, index) => (info, index)).Where(x => IsModuleText(x.info)).ToList();
        var result = new List<Module>();
        for (int m = 0; m < texts.Count; m++)
        {
            string name = texts.Count == 1 ? "main" : m switch
            {
                0 => "rtld",
                1 => "main",
                2 => "sdk",
                _ => "subsdk" + (m - 2),
            };
            int k = texts[m].index;
            long end = infos[k].NextAddress;
            for (int j = k + 1; j < infos.Count; j++)
            {
                MemoryInfo next = infos[j];
                if (next.Address != end || IsModuleText(next)
                    || next.Type is not (MemoryType.CODE_STATIC or MemoryType.CODE_MUTABLE))
                {
                    break;
                }
                end = next.NextAddress;
            }
            result.Add(new Module(name, infos[k].Address, infos[k].NextAddress, end));
        }
        return result;
    }

    private static bool IsModuleText(MemoryInfo info)
    {
        return info.Type == MemoryType.CODE_STATIC && info.Perm == 0b101;
    }
}
