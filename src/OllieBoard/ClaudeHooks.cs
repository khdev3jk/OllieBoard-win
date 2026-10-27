using System.Text.Json;
using System.Text.Json.Nodes;

namespace OllieBoard;

/// Optional Claude Code hooks that record each session's latest event in a file named after the `claude`
/// process id, so the matching character knows exactly when the agent works or waits. The hook command is this
/// app itself started with `--hook <event>`; it finds the Claude Code process above it and exits right away.
static class ClaudeHooks
{
    public static readonly string EventsDir = Path.Combine(Settings.SupportDir, "agents");
    public static readonly string SettingsPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    /// This executable with forward slashes, which every shell Claude Code may run hooks with accepts.
    private static string Exe => (Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "OllieBoard.exe")).Replace('\\', '/');

    /// Hook events we listen to, with the tool matcher where Claude Code expects one.
    private static readonly (string Name, string? Matcher)[] Events =
    [
        ("UserPromptSubmit", null), ("PreToolUse", "*"), ("PostToolUse", "*"),
        ("PermissionRequest", "*"), ("Notification", null), ("Stop", null),
    ];

    public static AgentState State(string hookEvent) => hookEvent switch
    {
        "UserPromptSubmit" or "PreToolUse" or "PostToolUse" => AgentState.Working,
        "PermissionRequest" or "Notification" => AgentState.NeedsInput,
        "Stop" => AgentState.Done,
        _ => AgentState.Idle,
    };

    /// The session's latest event; a file older than the process is left over from an earlier one with the same pid.
    public static string? LastEvent(int pid, DateTime? startedAt = null)
    {
        try
        {
            var path = Path.Combine(EventsDir, pid.ToString());
            if (startedAt is { } start && File.GetLastWriteTimeUtc(path) < start) return null;
            return File.ReadAllText(path).Trim();
        }
        catch { return null; }
    }

    public static void ClearEvent(int pid)
    {
        try { File.Delete(Path.Combine(EventsDir, pid.ToString())); } catch { }
    }

    public static bool IsInstalled
    {
        get
        {
            try { return File.ReadAllText(SettingsPath).Contains(Exe, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }
    }

    /// Adds our hooks next to whatever hooks already exist. The previous settings are kept as a backup.
    public static void Install()
    {
        Directory.CreateDirectory(EventsDir);
        JsonObject root;
        if (File.Exists(SettingsPath))
        {
            var text = File.ReadAllText(SettingsPath);
            // Refuse to touch a file we cannot parse rather than overwrite it.
            root = JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException($"{SettingsPath} is not a JSON object.");
            File.WriteAllText(SettingsPath + ".ollieboard-backup", text);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            root = new JsonObject();
        }

        var hooks = root["hooks"] as JsonObject ?? new JsonObject();
        foreach (var (name, matcher) in Events)
        {
            var entry = new JsonObject
            {
                ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = $"\"{Exe}\" --hook {name}" }),
            };
            if (matcher != null) entry["matcher"] = matcher;
            var list = hooks[name] as JsonArray ?? new JsonArray();
            list.Add(entry);
            hooks[name] = list;
        }
        root["hooks"] = hooks;
        var temp = SettingsPath + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
        File.Move(temp, SettingsPath, overwrite: true);
    }

    /// `OllieBoard.exe --hook <event>`: walks up from the hook to the Claude Code process so the event lands under
    /// that session's pid.
    public static int RunHook(string hookEvent)
    {
        try
        {
            // The outermost one, like AgentMonitor, which counts a session's own helper processes as part of it.
            var table = ProcessTable.Snapshot();
            var session = table.Ancestors(Environment.ProcessId)
                .LastOrDefault(pid => ProcessTable.Arguments(pid) is { } argv && AgentKind.Claude.Matches(argv));
            if (session != 0)
            {
                Directory.CreateDirectory(EventsDir);
                File.WriteAllText(Path.Combine(EventsDir, session.ToString()), hookEvent);
            }
        }
        catch { }
        return 0;
    }
}
