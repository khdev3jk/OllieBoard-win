using System.Runtime.InteropServices;
using System.Text;
using static OllieBoard.Native;

namespace OllieBoard;

enum AgentKind { Claude, Codex, Gemini, OpenCode, Cursor, Aider }

static class AgentKinds
{
    public static string Title(this AgentKind kind) => kind switch
    {
        AgentKind.Claude => "Claude",
        AgentKind.Codex => "Codex",
        AgentKind.Gemini => "Gemini",
        AgentKind.OpenCode => "OpenCode",
        AgentKind.Cursor => "Cursor",
        _ => "Aider",
    };

    /// Program names, and the npm package folder an interpreted install runs from.
    private static (string[] Names, string? Package) Signature(this AgentKind kind) => kind switch
    {
        AgentKind.Claude => (["claude"], @"\@anthropic-ai\claude-code\"),
        AgentKind.Codex => (["codex"], @"\@openai\codex\"),
        AgentKind.Gemini => (["gemini"], @"\@google\gemini-cli\"),
        AgentKind.OpenCode => (["opencode"], @"\opencode-ai\"),
        AgentKind.Cursor => (["cursor-agent"], null),
        _ => (["aider"], null),
    };

    /// Whether a process is a session of this agent, judged by the program in its argv (never by other
    /// arguments, so e.g. `findstr codex` doesn't count).
    public static bool Matches(this AgentKind kind, string[] argv)
    {
        if (argv.Length == 0) return false;
        var exe = Path.GetFileNameWithoutExtension(argv[0]).ToLowerInvariant();
        // npm / pip installs run as `node …\cli.js`, `python -m aider` or a `Scripts\aider.exe` launcher.
        var interpreted = exe is "node" or "bun" or "deno" || exe.StartsWith("python") || exe == "py";
        var script = argv.Length > 1 ? argv[1] : "";
        if (script == "-m" && argv.Length > 2) script = argv[2];
        var scriptName = Path.GetFileNameWithoutExtension(script).ToLowerInvariant();
        var (names, package) = kind.Signature();
        var named = names.Contains(exe) || (interpreted && (names.Contains(scriptName)
            || (package != null && script.Replace('/', '\\').Contains(package, StringComparison.OrdinalIgnoreCase))));
        // OpenCode's background server is not a session.
        return named && !(kind == AgentKind.OpenCode && argv.Contains("serve"));
    }
}

enum AgentState { Idle, Working, Done, NeedsInput }

sealed record AgentSession(int Pid, AgentKind Kind, string Project, AgentState State);

/// Polls the process table for AI coding agents. State comes from Claude Code hooks when they are installed
/// (see ClaudeHooks); otherwise it is guessed from CPU use of the agent and its children.
sealed class AgentMonitor
{
    public Action<Dictionary<int, AgentSession>>? OnUpdate;

    private System.Threading.Timer? timer;
    private const double Interval = 2;
    private readonly object gate = new();
    private volatile int generation;
    /// The generation `known` belongs to (only touched while holding `gate`).
    private int knownGeneration;

    // Only touched while holding `gate`.
    /// Classification per pid, redone only when the program at that pid changes.
    private Dictionary<int, (string Exe, AgentKind? Kind)> classified = new();
    private Dictionary<int, AgentSession> known = new();
    private readonly Dictionary<int, double> lastCpu = new();
    private readonly Dictionary<int, int> busyStreak = new();
    private readonly Dictionary<int, DateTime> lastBusy = new();

    public void Start()
    {
        timer?.Dispose();
        var run = Interlocked.Increment(ref generation);
        timer = new System.Threading.Timer(_ => Poll(run), null, TimeSpan.Zero, TimeSpan.FromSeconds(Interval));
    }

    public void Stop()
    {
        timer?.Dispose();
        timer = null;
        Interlocked.Increment(ref generation);   // the next Start begins from scratch
        Dispatch.Async(() => OnUpdate?.Invoke(new()));
    }

    private void Poll(int run)
    {
        if (!Monitor.TryEnter(gate)) return;   // the previous poll is still going
        try
        {
            if (run != generation) return;
            if (knownGeneration != run)
            {
                knownGeneration = run;
                known = new();
                lastCpu.Clear();
                busyStreak.Clear();
                lastBusy.Clear();
            }
            var table = ProcessTable.Snapshot();
            var found = new Dictionary<int, AgentKind>();
            var seen = new Dictionary<int, (string, AgentKind?)>();
            foreach (var (pid, entry) in table.Entries)
            {
                (string Exe, AgentKind? Kind) cached = classified.TryGetValue(pid, out var c) && c.Exe == entry.Exe ? c : (entry.Exe, Classify(pid));
                seen[pid] = cached;
                if (cached.Kind is { } kind) found[pid] = kind;
            }
            classified = seen;

            // A session's own helper processes (same agent further up the tree) are not new sessions.
            found = found.Where(f => !table.Ancestors(f.Key).Any(p => found.TryGetValue(p, out var k) && k == f.Value))
                .ToDictionary(f => f.Key, f => f.Value);

            double TreeCpu(int pid, int depth) =>
                ProcessTable.CpuSeconds(pid) + (depth > 16 ? 0 : table.Children(pid).Sum(c => TreeCpu(c, depth + 1)));

            var now = DateTime.UtcNow;
            var result = new Dictionary<int, AgentSession>();
            foreach (var (pid, kind) in found)
            {
                var session = known.GetValueOrDefault(pid) ?? new AgentSession(pid, kind, ProcessTable.ProjectName(pid), AgentState.Idle);
                var cpu = TreeCpu(pid, 0);
                if (ClaudeHooks.LastEvent(pid, ProcessTable.StartTime(pid)) is { } hookEvent)
                {
                    session = session with { State = ClaudeHooks.State(hookEvent) };
                }
                else if (lastCpu.TryGetValue(pid, out var previous))
                {
                    // More than ~3% of a core for two polls in a row counts as working; quiet for 12s after that means done.
                    var usage = (cpu - previous) / Interval;
                    busyStreak[pid] = usage > 0.03 ? busyStreak.GetValueOrDefault(pid) + 1 : 0;
                    if (busyStreak[pid] >= 2)
                    {
                        session = session with { State = AgentState.Working };
                        lastBusy[pid] = now;
                    }
                    else if (session.State == AgentState.Working && (now - lastBusy.GetValueOrDefault(pid, now)).TotalSeconds > 12)
                    {
                        session = session with { State = AgentState.Done };
                    }
                }
                lastCpu[pid] = cpu;
                result[pid] = session;
            }

            foreach (var pid in known.Keys.Where(p => !result.ContainsKey(p)))
            {
                lastCpu.Remove(pid);
                busyStreak.Remove(pid);
                lastBusy.Remove(pid);
                ClaudeHooks.ClearEvent(pid);
            }
            known = result;
            Dispatch.Async(() => { if (run == generation) OnUpdate?.Invoke(result); });
        }
        catch { }
        finally { Monitor.Exit(gate); }
    }

    /// Matches on the command line rather than the process name: npm installs run as node.exe.
    private static AgentKind? Classify(int pid)
    {
        if (pid == Environment.ProcessId) return null;
        var argv = ProcessTable.Arguments(pid);
        if (argv == null) return null;
        foreach (var kind in Enum.GetValues<AgentKind>()) if (kind.Matches(argv)) return kind;
        return null;
    }

    // MARK: Host app

    /// Brings the window the agent runs in (Windows Terminal, VS Code, a console window…) to the front.
    public static void FocusHostApp(int pid)
    {
        var table = ProcessTable.Snapshot();
        foreach (var p in table.Ancestors(pid).Prepend(pid))
        {
            // Explorer started the terminal; its windows are not the one we want.
            if (table.Entries.TryGetValue(p, out var entry) && entry.Exe.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)) break;
            if (MainWindow(p) is { } window) { Focus(window); return; }
        }
        // A classic console window belongs to conhost, outside the process tree.
        if (AttachConsole((uint)pid))
        {
            var console = GetConsoleWindow();
            FreeConsole();
            if (console != IntPtr.Zero && IsWindowVisible(console)) { Focus(console); return; }
        }
        // Windows Terminal took the console over (its default-terminal setting): bring its frontmost window up.
        if (FindWindow("CASCADIA_HOSTING_WINDOW_CLASS", null) is var terminal && terminal != IntPtr.Zero) Focus(terminal);
    }

    private static void Focus(IntPtr window)
    {
        if (IsIconic(window)) ShowWindow(window, SW_RESTORE);
        SetForegroundWindow(window);
    }

    /// A visible, titled, uncloaked top-level window of the process.
    private static IntPtr? MainWindow(int pid)
    {
        IntPtr? found = null;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner != pid || !IsWindowVisible(hwnd) || GetWindowTextLength(hwnd) == 0) return true;
            if ((GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TOOLWINDOW) != 0) return true;
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            found = hwnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }
}

/// One look at the process list: who is running, under whom.
sealed class ProcessTable
{
    public readonly Dictionary<int, (int Parent, string Exe)> Entries = new();
    private readonly Dictionary<int, List<int>> children = new();

    public static ProcessTable Snapshot()
    {
        var table = new ProcessTable();
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return table;
        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            for (var ok = Process32FirstW(snapshot, ref entry); ok; ok = Process32NextW(snapshot, ref entry))
                table.Entries[(int)entry.th32ProcessID] = ((int)entry.th32ParentProcessID, entry.szExeFile);
        }
        finally { CloseHandle(snapshot); }
        foreach (var (pid, (parent, _)) in table.Entries)
        {
            if (parent == pid) continue;
            if (!table.children.TryGetValue(parent, out var list)) table.children[parent] = list = new();
            list.Add(pid);
        }
        return table;
    }

    public IEnumerable<int> Children(int pid) => children.TryGetValue(pid, out var list) ? list : [];

    /// Parent, grandparent… (a stale parent id may point at an unrelated process, so the walk is bounded).
    public IEnumerable<int> Ancestors(int pid)
    {
        var visited = new HashSet<int> { pid };
        var p = pid;
        while (Entries.TryGetValue(p, out var e) && e.Parent > 4 && visited.Add(e.Parent) && visited.Count < 64)
        {
            p = e.Parent;
            yield return p;
        }
    }

    /// Total CPU time (user + kernel) the process has used, in seconds.
    public static double CpuSeconds(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return 0;
        try { return GetProcessTimes(h, out _, out _, out var kernel, out var user) ? (kernel + user) / 1e7 : 0; }
        finally { CloseHandle(h); }
    }

    /// When the process started (UTC), or null if it can't be opened.
    public static DateTime? StartTime(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try { return GetProcessTimes(h, out var creation, out _, out _, out _) ? DateTime.FromFileTimeUtc(creation) : null; }
        finally { CloseHandle(h); }
    }

    /// argv of a process.
    public static string[]? Arguments(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var buffer = new byte[64 * 1024];
            // Pinned for the call too: the result points into the buffer at the address it had then.
            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                if (NtQueryInformationProcess(h, ProcessCommandLineInformation, buffer, buffer.Length, out _) != 0) return null;
                // UNICODE_STRING { ushort Length; ushort MaximumLength; IntPtr Buffer } pointing into `buffer`.
                var length = BitConverter.ToUInt16(buffer, 0);
                var start = handle.AddrOfPinnedObject();
                var text = Marshal.ReadIntPtr(start, IntPtr.Size);
                var offset = (int)(text.ToInt64() - start.ToInt64());
                if (offset < 0 || offset + length > buffer.Length) return null;
                return SplitCommandLine(Encoding.Unicode.GetString(buffer, offset, length));
            }
            finally { handle.Free(); }
        }
        finally { CloseHandle(h); }
    }

    /// Name of the agent's working directory, used as the project label (read from the process environment block).
    public static string ProjectName(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, pid);
        if (h == IntPtr.Zero) return "";
        try
        {
            if (IsWow64Process(h, out var wow64) && wow64) return "";
            var info = new byte[48];   // PROCESS_BASIC_INFORMATION: PebBaseAddress at 8
            if (NtQueryInformationProcess(h, ProcessBasicInformation, info, info.Length, out _) != 0) return "";
            var peb = new IntPtr(BitConverter.ToInt64(info, 8));
            var parameters = new IntPtr(BitConverter.ToInt64(ReadMemory(h, peb + 0x20, 8), 0));
            // RTL_USER_PROCESS_PARAMETERS.CurrentDirectory.DosPath: UNICODE_STRING at 0x38.
            var dosPath = ReadMemory(h, parameters + 0x38, 16);
            var length = BitConverter.ToUInt16(dosPath, 0);
            var text = new IntPtr(BitConverter.ToInt64(dosPath, 8));
            if (length == 0 || length > 4096) return "";
            var path = Encoding.Unicode.GetString(ReadMemory(h, text, length)).TrimEnd('\\');
            var name = Path.GetFileName(path);
            return string.IsNullOrEmpty(name) || path.Length <= 2 ? "" : name;
        }
        catch { return ""; }
        finally { CloseHandle(h); }
    }

    private static byte[] ReadMemory(IntPtr process, IntPtr address, int length)
    {
        var buffer = new byte[length];
        if (!ReadProcessMemory(process, address, buffer, length, out _)) throw new IOException("ReadProcessMemory failed");
        return buffer;
    }
}
