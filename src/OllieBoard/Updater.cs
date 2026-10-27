using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace OllieBoard;

/// Finds a newer release on GitHub and installs it in one click: downloads its Windows installer, starts it
/// silently and quits; the installer replaces this copy and opens the new one.
sealed class Updater
{
    public abstract record State
    {
        public sealed record Idle : State;
        public sealed record Checking : State;
        public sealed record UpToDate : State;
        public sealed record Installing : State;
        public sealed record Available(string Version, string Url) : State;
        public sealed record Failed(string Message) : State;
    }

    private const string LatestRelease = "https://api.github.com/repos/khdev3jk/OllieBoard-win/releases/latest";
    private static readonly HttpClient Http = CreateClient();

    public static string CurrentVersion
    {
        get
        {
            var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0";
            return version.Split('+')[0];
        }
    }

    /// Only an installed copy can replace itself (not one run from the build folder).
    public static bool CanUpdate => File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    public State Current { get; private set; } = new State.Idle();
    /// Called on the main thread after an install fails, with the reason.
    public Action<string>? OnInstallFailed;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"OllieBoard/{CurrentVersion}");
        return client;
    }

    public void Check()
    {
        if (Current is State.Checking or State.Installing) return;
        Current = new State.Checking();
        Task.Run(async () =>
        {
            State result;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, LatestRelease);
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                using var response = await Http.SendAsync(request);
                response.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
                var installer = doc.RootElement.GetProperty("assets").EnumerateArray()
                    .Select(a => (Name: a.GetProperty("name").GetString() ?? "", Url: a.GetProperty("browser_download_url").GetString() ?? ""))
                    .FirstOrDefault(a => a.Name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase));
                if (installer.Url is not { Length: > 0 }) throw new InvalidOperationException("No release with a Windows installer found");
                var version = tag.Trim('v', 'V');
                result = IsNewer(version, CurrentVersion) ? new State.Available(version, installer.Url) : new State.UpToDate();
            }
            catch (Exception e) { result = new State.Failed(e.Message); }
            Dispatch.Async(() => Current = result);
        });
    }

    public void Install()
    {
        if (Current is not State.Available available || !CanUpdate) return;
        Current = new State.Installing();
        Task.Run(async () =>
        {
            try
            {
                var file = Path.Combine(Path.GetTempPath(), $"OllieBoard-{available.Version}-setup.exe");
                await using (var download = await Http.GetStreamAsync(available.Url))
                await using (var output = File.Create(file))
                    await download.CopyToAsync(output);
                Process.Start(new ProcessStartInfo(file, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART") { UseShellExecute = true });
                Dispatch.Async(Application.Exit);
            }
            catch (Exception e)
            {
                Dispatch.Async(() =>
                {
                    Current = new State.Failed(e.Message);
                    OnInstallFailed?.Invoke(e.Message);
                });
            }
        });
    }

    /// "1.10" is newer than "1.9"; missing parts count as 0.
    public static bool IsNewer(string version, string current)
    {
        int[] Parts(string v) => v.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
        var a = Parts(version);
        var b = Parts(current);
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y) return x > y;
        }
        return false;
    }
}
