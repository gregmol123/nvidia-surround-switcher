using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ScreenSwitcher;

public sealed record GridDisplay(uint Id, int OverlapX, int OverlapY, int Rotation);

// A display grid as the driver sees it: Surround is one grid of several monitors, a monitor on its own is a 1x1 grid.
public sealed record Grid(int Rows, int Columns, uint Flags, int Width, int Height, int Bpp, int Frequency, List<GridDisplay> Displays);

// The Mosaic (Surround) calls of the NVIDIA driver. Struct layouts and function ids come from NVIDIA's public nvapi.h and nvapi_interface.h.
public static unsafe class NvApi
{
    const uint InitializeId = 0x0150E828;
    const uint GetErrorMessageId = 0x6C2D048C;
    const uint EnumDisplayGridsId = 0xDF2887AF;
    const uint ValidateDisplayGridsId = 0xCF43903D;
    const uint SetDisplayGridsId = 0x4D959A89;

    // NV_MOSAIC_GRID_TOPO.driverReloadAllowed is "undefined on get", and the driver answers NVAPI_INVALID_ARGUMENT
    // when it is sent back together with the acceleratePrimaryDisplay flag that Surround has here.
    const uint DriverReloadAllowed = 1u << 3;
    const uint ProblemNoDisplayConnected = 1u << 5;   // NV_MOSAIC_DISPLAYCAPS_PROBLEM_NO_DISPLAY_CONNECTED

    [StructLayout(LayoutKind.Sequential)]
    struct RawDisplay
    {
        public uint Version, DisplayId;
        public int OverlapX, OverlapY, Rotation;
        public uint CloneGroup;
        public int PixelShiftType;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RawSetting
    {
        public uint Version, Width, Height, Bpp, Frequency;
    }

    [InlineArray(64)]   // NV_MOSAIC_MAX_DISPLAYS
    struct RawDisplays { RawDisplay _first; }

    [StructLayout(LayoutKind.Sequential)]
    struct RawGrid
    {
        public uint Version, Rows, Columns, DisplayCount, Flags;
        public RawDisplays Displays;
        public RawSetting Setting;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RawStatusDisplay
    {
        public uint DisplayId, ErrorFlags, WarningFlags, Bits;
    }

    [InlineArray(128)]   // NVAPI_MAX_DISPLAYS
    struct RawStatusDisplays { RawStatusDisplay _first; }

    [StructLayout(LayoutKind.Sequential)]
    struct RawStatus
    {
        public uint Version, ErrorFlags, WarningFlags, DisplayCount;
        public RawStatusDisplays Displays;
    }

    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr QueryInterface(uint id);

    static bool _ready;

    // MAKE_NVAPI_VERSION from nvapi.h: the struct size with the version number on top.
    static uint Version<T>(uint number) where T : unmanaged => (uint)sizeof(T) | (number << 16);

    public static List<Grid> ReadGrids()
    {
        EnsureReady();
        var enumGrids = (delegate* unmanaged[Cdecl]<RawGrid*, uint*, int>)Function(EnumDisplayGridsId);

        uint count = 0;
        Check(enumGrids(null, &count), "reading the screen layout");

        var raw = new RawGrid[count];
        for (var i = 0; i < raw.Length; i++) raw[i].Version = Version<RawGrid>(2);
        fixed (RawGrid* grids = raw) Check(enumGrids(grids, &count), "reading the screen layout");

        return raw.Take((int)count).Select(FromRaw).ToList();
    }

    // Asks the driver about the grids without applying anything, and throws with the reason if it would refuse them.
    public static void Validate(List<Grid> grids)
    {
        EnsureReady();
        var validate = (delegate* unmanaged[Cdecl]<uint, RawGrid*, RawStatus*, uint, int>)Function(ValidateDisplayGridsId);

        var raw = grids.Select(ToRaw).ToArray();
        var status = new RawStatus[raw.Length];
        for (var i = 0; i < status.Length; i++) status[i].Version = Version<RawStatus>(1);

        fixed (RawGrid* rawGrids = raw)
        fixed (RawStatus* rawStatus = status)
            Check(validate(0, rawGrids, rawStatus, (uint)raw.Length), "checking the screen layout");

        for (var i = 0; i < status.Length; i++)
        {
            var displayErrors = 0u;
            for (var d = 0; d < status[i].DisplayCount && d < 128; d++) displayErrors |= status[i].Displays[d].ErrorFlags;

            if ((displayErrors & ProblemNoDisplayConnected) != 0)
                throw new InvalidOperationException("One of the monitors is switched off or disconnected. Turn it on and try again.");
            if (status[i].ErrorFlags != 0 || displayErrors != 0)
                throw new InvalidOperationException($"The NVIDIA driver does not accept this screen layout (codes 0x{status[i].ErrorFlags:X} and 0x{displayErrors:X}).");
        }
    }

    public static void Apply(List<Grid> grids)
    {
        EnsureReady();
        var set = (delegate* unmanaged[Cdecl]<RawGrid*, uint, uint, int>)Function(SetDisplayGridsId);

        var raw = grids.Select(ToRaw).ToArray();
        fixed (RawGrid* rawGrids = raw) Check(set(rawGrids, (uint)raw.Length, 0), "switching the screens");
    }

    static Grid FromRaw(RawGrid raw)
    {
        var displays = new List<GridDisplay>();
        for (var i = 0; i < raw.DisplayCount && i < 64; i++)
        {
            var display = raw.Displays[i];
            displays.Add(new GridDisplay(display.DisplayId, display.OverlapX, display.OverlapY, display.Rotation));
        }
        return new Grid((int)raw.Rows, (int)raw.Columns, raw.Flags & ~DriverReloadAllowed,
            (int)raw.Setting.Width, (int)raw.Setting.Height, (int)raw.Setting.Bpp, (int)raw.Setting.Frequency, displays);
    }

    static RawGrid ToRaw(Grid grid)
    {
        var raw = new RawGrid
        {
            Version = Version<RawGrid>(2),
            Rows = (uint)grid.Rows,
            Columns = (uint)grid.Columns,
            DisplayCount = (uint)grid.Displays.Count,
            Flags = grid.Flags,
            Setting = new RawSetting
            {
                Version = Version<RawSetting>(1),
                Width = (uint)grid.Width,
                Height = (uint)grid.Height,
                Bpp = (uint)grid.Bpp,
                Frequency = (uint)grid.Frequency,
            },
        };
        for (var i = 0; i < grid.Displays.Count; i++)
        {
            var display = grid.Displays[i];
            raw.Displays[i] = new RawDisplay
            {
                Version = Version<RawDisplay>(2),
                DisplayId = display.Id,
                OverlapX = display.OverlapX,
                OverlapY = display.OverlapY,
                Rotation = display.Rotation,
            };
        }
        return raw;
    }

    static void EnsureReady()
    {
        if (_ready) return;
        Check(((delegate* unmanaged[Cdecl]<int>)Function(InitializeId))(), "starting up");
        _ready = true;
    }

    static IntPtr Function(uint id)
    {
        var address = QueryInterface(id);
        if (address == IntPtr.Zero) throw new InvalidOperationException($"The NVIDIA driver has no function 0x{id:X8}.");
        return address;
    }

    static void Check(int status, string during)
    {
        if (status != 0) throw new InvalidOperationException($"The NVIDIA driver reported an error while {during}: {ErrorName(status)}.");
    }

    static string ErrorName(int status)
    {
        var text = stackalloc byte[64];
        var getMessage = (delegate* unmanaged[Cdecl]<int, byte*, int>)Function(GetErrorMessageId);
        return getMessage(status, text) == 0 ? Marshal.PtrToStringAnsi((IntPtr)text) ?? status.ToString() : status.ToString();
    }
}
