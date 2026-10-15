// Sign extension of the int shifts into the long flags is intentional (matches Java).
#pragma warning disable CS0675

namespace Noexs.Misc;

public enum BreakpointType
{
    UNLINKED_INSN_ADDRESS_MATCH = 0b0000,
    LINKED_INSN_ADDRESS_MATCH = 0b0001,
    UNLINKED_CONTEXT_IDR_MATCH = 0b0010,
    LINKED_CONTEXT_IDR_MATCH = 0b0011,
    UNLINKED_ISNS_ADDRESS_MISMATCH = 0b0100,
    LINKED_INSN_ADDRESS_MISMATCH = 0b0101,
    UNLINKED_VMID_MATCH = 0b1000,
    LINKED_VMID_MATCH = 0b1001,
    UNLINKED_VMID_CONTEXT_IDR_MATCH = 0b1010,
    LINKED_VMID_CONTEXT_IDR_MATCH = 0b1011,
}

public sealed class BreakpointFlagBuilder
{
    private bool enabled = true;
    private int bas = 0xF;
    private int lbn;
    private BreakpointType type = BreakpointType.UNLINKED_INSN_ADDRESS_MATCH;

    public BreakpointFlagBuilder SetEnabled(bool enabled)
    {
        this.enabled = enabled;
        return this;
    }

    public BreakpointFlagBuilder SetAddressSelect(int bas)
    {
        this.bas = bas;
        return this;
    }

    public BreakpointFlagBuilder SetLinkedBreakpointNumber(int linked)
    {
        lbn = linked;
        return this;
    }

    public BreakpointFlagBuilder SetBreakpointType(BreakpointType t)
    {
        type = t;
        return this;
    }

    public long GetFlag()
    {
        long res = 0;
        if (enabled)
        {
            res |= 0x1;
        }
        res |= bas << 5;
        res |= lbn << 16;
        res |= (int)type << 20;
        return res;
    }
}

public sealed class WatchpointFlagBuilder
{
    public enum MatchType
    {
        LOAD = 0b01,
        STORE = 0b10,
        ALL = 0b11,
    }

    private bool enabled;
    private MatchType lsc = MatchType.ALL;
    private int bas;
    private int lbn;
    private int mask;

    public WatchpointFlagBuilder SetEnabled(bool enabled)
    {
        this.enabled = enabled;
        return this;
    }

    public WatchpointFlagBuilder SetAddressSelect(int bas)
    {
        this.bas = bas;
        return this;
    }

    public WatchpointFlagBuilder SetAccessContol(MatchType type)
    {
        lsc = type;
        return this;
    }

    public WatchpointFlagBuilder SetLinkedBreakpointNumber(int number)
    {
        lbn = number;
        return this;
    }

    public WatchpointFlagBuilder SetAddressMask(int mask)
    {
        this.mask = mask;
        return this;
    }

    public long GetFlag()
    {
        long res = 0;

        if (enabled)
        {
            res |= 0x1;
        }

        res |= (int)lsc << 3;

        res |= bas << 5;
        res |= lbn << 16;
        res |= mask << 24;

        return res;
    }
}
