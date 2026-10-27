using System.Runtime.InteropServices;
using System.Text;
using static OllieBoard.Native;

namespace OllieBoard;

readonly record struct DesktopIcon(string Name, PointD Center);

/// Reads and moves the real desktop icons. Explorer shows them in a list view (SysListView32 inside
/// SHELLDLL_DefView); its items are read through memory shared with Explorer and moved with list-view messages.
/// All Explorer calls run one at a time on a background thread so a busy Explorer never stalls the animation.
sealed class DesktopIcons
{
    private readonly SerialQueue queue = new("ollieboard.desktop");
    public List<DesktopIcon> Icons { get; private set; } = new();
    /// Explorer's "Auto arrange icons" pins icons in place.
    public bool IconsArePinned { get; private set; }
    /// Explorer's "Align icons to grid": moved icons land on the grid, so carried icons move in steps.
    public bool IconsSnapToGrid { get; private set; }
    public string? LastError { get; private set; }

    private readonly Dictionary<string, PointD> liveTarget = new();
    private bool liveBusy;
    private bool refreshing;
    /// Waiting for the refresh in progress (which may have started before the change they want to see).
    private readonly List<Action> pendingCompletions = new();
    private bool refreshAgain;

    // Only touched on `queue`.
    private DesktopListView? listView;

    private DesktopListView ListView()
    {
        if (listView is { IsAlive: true }) return listView;
        listView?.Dispose();
        listView = DesktopListView.Open();
        return listView;
    }

    // MARK: Reading

    public void Refresh(Action? completion = null)
    {
        // Never queue up reads behind one Explorer hasn't answered yet; callers waiting on a result get the next one.
        if (refreshing)
        {
            if (completion != null) { pendingCompletions.Add(completion); refreshAgain = true; }
            return;
        }
        refreshing = true;
        queue.Async(() =>
        {
            string? error = null;
            List<DesktopIcon> icons = new();
            var pinned = false;
            var snaps = false;
            try
            {
                var view = ListView();
                pinned = view.AutoArrange;
                snaps = view.SnapToGrid;
                icons = view.ReadAll().Select(i => new DesktopIcon(i.Name, i.Center)).ToList();
            }
            catch (Exception e) { error = e.Message; }
            Dispatch.Async(() =>
            {
                refreshing = false;
                LastError = error;
                if (error == null)
                {
                    Icons = icons;
                    IconsArePinned = pinned;
                    IconsSnapToGrid = snaps;
                }
                completion?.Invoke();
                if (refreshAgain)
                {
                    refreshAgain = false;
                    var waiting = pendingCompletions.ToList();
                    pendingCompletions.Clear();
                    Refresh(() => waiting.ForEach(w => w()));
                }
            });
        });
    }

    // MARK: Moving

    /// Moves an icon through a list of points, then reports where it really ended up.
    public void Move(string name, List<PointD> path, double stepDelay = 0.02, Action<PointD?>? completion = null) =>
        MoveAll(new Dictionary<string, List<PointD>> { [name] = path }, stepDelay,
            finals => completion?.Invoke(finals.TryGetValue(name, out var p) ? p : null));

    /// Moves several icons together, a step along each path at a time, then reports where each really ended up.
    public void MoveAll(Dictionary<string, List<PointD>> paths, double stepDelay = 0.02,
        Action<Dictionary<string, PointD>>? completion = null)
    {
        var entries = paths.Where(p => p.Value.Count > 0).ToList();
        if (entries.Count == 0) { completion?.Invoke(new()); return; }
        queue.Async(() =>
        {
            var finals = new Dictionary<string, PointD>();
            try
            {
                var view = ListView();
                var steps = entries.Max(e => e.Value.Count);
                for (var step = 0; step < steps; step++)
                {
                    foreach (var (name, path) in entries)
                        if (step < path.Count) view.Move(name, path[step]);
                    if (step < steps - 1 && stepDelay > 0) Thread.Sleep(TimeSpan.FromSeconds(stepDelay));
                }
                foreach (var (name, _) in entries)
                    if (view.Center(name) is { } p) finals[name] = p;
            }
            catch { }
            Dispatch.Async(() =>
            {
                Icons = Icons.Select(i => finals.TryGetValue(i.Name, out var p) ? i with { Center = p } : i).ToList();
                completion?.Invoke(finals);
            });
        });
    }

    /// Follows a moving target (e.g. an icon being carried). Calls are coalesced so Explorer never queues up.
    public void MoveLive(string name, PointD point)
    {
        liveTarget[name] = point;
        PumpLive();
    }

    public void CancelLive(string name) => liveTarget.Remove(name);

    private void PumpLive()
    {
        if (liveBusy || liveTarget.Count == 0) return;
        var (name, point) = liveTarget.First();
        liveTarget.Remove(name);
        liveBusy = true;
        Move(name, [point], 0, _ =>
        {
            liveBusy = false;
            PumpLive();
        });
    }

    /// Turns off "Auto arrange icons" so icons can be moved. Only called on explicit user request.
    public void DisableAutoArrange(Action<bool> completion)
    {
        // Shell COM objects want the UI (STA) thread.
        var ok = ShellDesktop.ClearFolderFlags(ShellDesktop.FWF_AUTOARRANGE);
        Refresh(() => completion(ok && !IconsArePinned));
    }

    /// Turns off "Align icons to grid" so icons go exactly where they are put. Only called on explicit user request.
    public void DisableSnapToGrid(Action<bool> completion)
    {
        var ok = ShellDesktop.ClearFolderFlags(ShellDesktop.FWF_SNAPTOGRID);
        Refresh(() => completion(ok && !IconsSnapToGrid));
    }
}

/// Finding the desktop's windows and talking to its shell view.
static class ShellDesktop
{
    /// The desktop icons' SHELLDLL_DefView and the top-level window that hosts it (Progman, or a WorkerW on
    /// Windows 10 with an animated or slideshow wallpaper).
    public static (IntPtr Host, IntPtr DefView) Find()
    {
        var progman = FindWindow("Progman", null);
        var defView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView != IntPtr.Zero) return (progman, defView);
        var found = (IntPtr.Zero, IntPtr.Zero);
        EnumWindows((hwnd, _) =>
        {
            if (ClassName(hwnd) != "WorkerW") return true;
            var view = FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (view == IntPtr.Zero) return true;
            found = (hwnd, view);
            return false;
        }, IntPtr.Zero);
        return found;
    }

    public static IntPtr FindHost() => Find().Host;

    // COM: IShellWindows → desktop browser → active shell view → IFolderView2.SetCurrentFolderFlags.

    private const int SWC_DESKTOP = 8, SWFO_NEEDDISPATCH = 1;
    public const uint FWF_AUTOARRANGE = 0x1, FWF_SNAPTOGRID = 0x4;
    private static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");

    /// Turns off the given desktop view flags (FOLDERFLAGS).
    public static bool ClearFolderFlags(uint flags)
    {
        var objects = new List<object>();
        T Keep<T>(object o) { objects.Add(o); return (T)o; }
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"))!;
            var windows = Keep<IShellWindows>(Activator.CreateInstance(type)!);
            object location = 0;   // CSIDL_DESKTOP
            object empty = null!;   // VT_EMPTY
            var provider = Keep<IServiceProvider>(windows.FindWindowSW(ref location, ref empty, SWC_DESKTOP, out _, SWFO_NEEDDISPATCH));
            var sid = SID_STopLevelBrowser;
            var iid = typeof(IShellBrowser).GUID;
            var browser = Keep<IShellBrowser>(provider.QueryService(ref sid, ref iid));
            var view = Keep<IFolderView2>(browser.QueryActiveShellView());
            return view.SetCurrentFolderFlags(flags, 0) >= 0;
        }
        catch { return false; }
        finally
        {
            foreach (var o in objects.Distinct()) Marshal.ReleaseComObject(o);
        }
    }

    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IShellWindows
    {
        int Count { get; }
        [return: MarshalAs(UnmanagedType.IDispatch)] object Item(object index);
        [return: MarshalAs(UnmanagedType.IUnknown)] object _NewEnum();
        int Register([MarshalAs(UnmanagedType.IDispatch)] object pid, int hwnd, int swClass);
        int RegisterPending(int threadId, ref object loc, ref object locRoot, int swClass);
        void Revoke(int cookie);
        void OnNavigate(int cookie, ref object loc);
        void OnActivated(int cookie, bool active);
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object FindWindowSW(ref object loc, ref object locRoot, int swClass, out int hwnd, int options);
        void OnCreated(int cookie, [MarshalAs(UnmanagedType.IUnknown)] object punk);
        bool ProcessAttachDetach(bool attach);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [return: MarshalAs(UnmanagedType.IUnknown)] object QueryService(ref Guid service, ref Guid riid);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        // IOleWindow
        void GetWindow();
        void ContextSensitiveHelp();
        // IShellBrowser, in vtable order; only QueryActiveShellView is called.
        void InsertMenusSB();
        void SetMenuSB();
        void RemoveMenusSB();
        void SetStatusTextSB();
        void EnableModelessSB();
        void TranslateAcceleratorSB();
        void BrowseObject();
        void GetViewStateStream();
        void GetControlWindow();
        void SendControlMsg();
        [return: MarshalAs(UnmanagedType.IUnknown)] object QueryActiveShellView();
        void OnViewWindowActive();
        void SetToolbarItems();
    }

    [ComImport, Guid("1af3a467-214f-4298-908e-06b03e0b39f9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFolderView2
    {
        // IFolderView, in vtable order.
        void GetCurrentViewMode();
        void SetCurrentViewMode();
        void GetFolder();
        void Item();
        void ItemCount();
        void Items();
        void GetSelectionMarkedItem();
        void GetFocusedItem();
        void GetItemPosition();
        void GetSpacing();
        void GetDefaultSpacing();
        void GetAutoArrange();
        void SelectItem();
        void SelectAndPositionItems();
        // IFolderView2, in vtable order; only SetCurrentFolderFlags is called.
        void SetGroupBy();
        void GetGroupBy();
        void SetViewProperty();
        void GetViewProperty();
        void SetTileViewProperties();
        void SetExtendedTileViewProperties();
        void SetText();
        [PreserveSig] int SetCurrentFolderFlags(uint mask, uint flags);
    }
}

/// The desktop's list view, with a block of memory inside Explorer for the messages that pass pointers.
sealed class DesktopListView : IDisposable
{
    private const uint LVM_FIRST = 0x1000;
    private const uint LVM_GETITEMCOUNT = LVM_FIRST + 4, LVM_GETITEMRECT = LVM_FIRST + 14,
        LVM_SETITEMPOSITION = LVM_FIRST + 15, LVM_GETITEMPOSITION = LVM_FIRST + 16, LVM_GETITEMTEXTW = LVM_FIRST + 115;
    private const uint LVM_GETEXTENDEDLISTVIEWSTYLE = LVM_FIRST + 55;
    private const int LVS_AUTOARRANGE = 0x100, LVS_EX_SNAPTOGRID = 0x80000, LVIR_ICON = 1, LVIF_TEXT = 1;
    private const uint SMTO_ABORTIFHUNG = 0x2;
    private const int TextChars = 520;
    // Layout of the shared block: an LVITEMW, then a POINT/RECT, then the text buffer.
    private const int ItemOffset = 0, PointOffset = 128, TextOffset = 256, BlockSize = TextOffset + TextChars * 2;

    private readonly IntPtr hwnd, process, remote;

    /// Item index and the offset from its position (top-left) to its icon's center, from the last read, by name.
    private readonly Dictionary<string, (int Index, double Dx, double Dy)> items = new();

    private DesktopListView(IntPtr hwnd, IntPtr process, IntPtr remote)
    {
        this.hwnd = hwnd;
        this.process = process;
        this.remote = remote;
    }

    public static DesktopListView Open()
    {
        if (!Environment.Is64BitProcess) throw new InvalidOperationException("OllieBoard must run as a 64-bit process.");
        var (_, defView) = ShellDesktop.Find();
        var hwnd = FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException("Desktop icons not found (Explorer isn't running?).");
        GetWindowThreadProcessId(hwnd, out var pid);
        var process = OpenProcess(PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_QUERY_INFORMATION, false, (int)pid);
        if (process == IntPtr.Zero) throw new InvalidOperationException($"Can't open Explorer (error {Marshal.GetLastWin32Error()}).");
        var remote = VirtualAllocEx(process, IntPtr.Zero, (UIntPtr)BlockSize, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (remote == IntPtr.Zero)
        {
            CloseHandle(process);
            throw new InvalidOperationException($"Can't share memory with Explorer (error {Marshal.GetLastWin32Error()}).");
        }
        return new DesktopListView(hwnd, process, remote);
    }

    public bool IsAlive => IsWindow(hwnd);

    public bool AutoArrange => (GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64() & LVS_AUTOARRANGE) != 0;

    public bool SnapToGrid => (Send(LVM_GETEXTENDEDLISTVIEWSTYLE, IntPtr.Zero, IntPtr.Zero).ToInt64() & LVS_EX_SNAPTOGRID) != 0;

    public void Dispose()
    {
        VirtualFreeEx(process, remote, UIntPtr.Zero, MEM_RELEASE);
        CloseHandle(process);
    }

    private IntPtr Send(uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (SendMessageTimeout(hwnd, msg, wParam, lParam, SMTO_ABORTIFHUNG, 2000, out var result) == IntPtr.Zero)
            throw new TimeoutException("Explorer isn't responding.");
        return result;
    }

    private byte[] Read(int offset, int length)
    {
        var buffer = new byte[length];
        ReadProcessMemory(process, remote + offset, buffer, length, out _);
        return buffer;
    }

    private void Write(int offset, byte[] data) => WriteProcessMemory(process, remote + offset, data, data.Length, out _);

    private string Text(int index)
    {
        // LVITEMW (64-bit): mask @0, iItem @4, iSubItem @8, pszText @24, cchTextMax @32.
        var item = new byte[88];
        BitConverter.GetBytes(LVIF_TEXT).CopyTo(item, 0);
        BitConverter.GetBytes(index).CopyTo(item, 4);
        BitConverter.GetBytes((remote + TextOffset).ToInt64()).CopyTo(item, 24);
        BitConverter.GetBytes(TextChars).CopyTo(item, 32);
        Write(ItemOffset, item);
        var length = Send(LVM_GETITEMTEXTW, index, remote + ItemOffset).ToInt32();
        return length <= 0 ? "" : Encoding.Unicode.GetString(Read(TextOffset, Math.Min(length, TextChars - 1) * 2));
    }

    private Point Position(int index)
    {
        Send(LVM_GETITEMPOSITION, index, remote + PointOffset);
        var data = Read(PointOffset, 8);
        return new Point(BitConverter.ToInt32(data, 0), BitConverter.ToInt32(data, 4));
    }

    private Rectangle IconRect(int index)
    {
        Write(PointOffset, BitConverter.GetBytes(LVIR_ICON));
        Send(LVM_GETITEMRECT, index, remote + PointOffset);
        var data = Read(PointOffset, 16);
        return Rectangle.FromLTRB(BitConverter.ToInt32(data, 0), BitConverter.ToInt32(data, 4),
            BitConverter.ToInt32(data, 8), BitConverter.ToInt32(data, 12));
    }

    /// Screen pixels of the list view's client origin.
    private Point Origin()
    {
        var p = new POINT();
        ClientToScreen(hwnd, ref p);
        return new Point(p.X, p.Y);
    }

    /// Every icon with a unique name (repeats get " (2)", " (3)"…) and its center in app coordinates.
    public List<(string Name, PointD Center)> ReadAll()
    {
        items.Clear();
        if (!IsWindowVisible(hwnd)) return [];   // "Show desktop icons" is off
        var origin = Origin();
        var count = Send(LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero).ToInt32();
        var result = new List<(string, PointD)>();
        for (var i = 0; i < count; i++)
        {
            var name = Text(i);
            for (var n = 2; items.ContainsKey(name); n++) name = $"{Text(i)} ({n})";
            var position = Position(i);
            var icon = IconRect(i);
            var cx = icon.Left + icon.Width / 2.0;
            var cy = icon.Top + icon.Height / 2.0;
            items[name] = (i, cx - position.X, cy - position.Y);
            result.Add((name, Screens.FromPixels(origin.X + cx, origin.Y + cy)));
        }
        return result;
    }

    /// The item for `name`, re-reading the list if it changed since the last read.
    private (int Index, double Dx, double Dy)? Item(string name)
    {
        if (items.TryGetValue(name, out var item) && item.Index < Send(LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero).ToInt32())
        {
            var text = Text(item.Index);
            if (text == name || name.StartsWith(text + " (")) return item;
        }
        ReadAll();
        return items.TryGetValue(name, out item) ? item : null;
    }

    /// Puts the icon's center at `center` (app coordinates).
    public void Move(string name, PointD center)
    {
        if (Item(name) is not { } item) return;
        var origin = Origin();
        var (px, py) = Screens.ToPixels(center);
        var x = (int)Math.Round(px - origin.X - item.Dx);
        var y = (int)Math.Round(py - origin.Y - item.Dy);
        x = Math.Clamp(x, 0, 0x7FFF);
        y = Math.Clamp(y, 0, 0x7FFF);
        Send(LVM_SETITEMPOSITION, item.Index, (IntPtr)((y << 16) | x));
    }

    /// Where the icon's center is now (app coordinates).
    public PointD? Center(string name)
    {
        if (Item(name) is not { } item) return null;
        var origin = Origin();
        var p = Position(item.Index);
        return Screens.FromPixels(origin.X + p.X + item.Dx, origin.Y + p.Y + item.Dy);
    }
}
