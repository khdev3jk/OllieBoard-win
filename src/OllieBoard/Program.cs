namespace OllieBoard;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // Started by a Claude Code hook: record the event and leave without any UI.
        if (args.Length >= 2 && args[0] == "--hook") return ClaudeHooks.RunHook(args[1]);

        using var instance = new Mutex(true, @"Local\OllieBoard", out var first);
        if (!first) return 0;

        ApplicationConfiguration.Initialize();
        Native.timeBeginPeriod(1);   // a steady 30 fps animation clock
        Screens.Init();
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        Dispatch.Init(SynchronizationContext.Current!);
        Application.Run(new TrayApp());
        return 0;
    }
}
