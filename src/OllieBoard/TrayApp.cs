using System.Reflection;

namespace OllieBoard;

/// The notification-area icon and its menu (the menu bar item on macOS).
sealed class TrayApp : ApplicationContext
{
    private readonly World world = new();
    private readonly NotifyIcon tray;
    private readonly ContextMenuStrip menu = new();
    private readonly Updater updater = new();
    private readonly System.Windows.Forms.Timer updateTimer = new() { Interval = 6 * 3600 * 1000 };
    private const string EnabledKey = "enabledCharacters";
    /// Set by a checkbox item so the menu it is in stays open after the click.
    private bool keepOpen;

    private HashSet<string> EnabledIds
    {
        get => (Settings.GetStringList(EnabledKey) ?? ["nezuko"]).ToHashSet();
        set => Settings.SetStringList(EnabledKey, value);
    }

    public TrayApp()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("OllieBoard.ico");
        tray = new NotifyIcon
        {
            Icon = stream != null ? new Icon(stream, SystemInformation.SmallIconSize) : SystemIcons.Application,
            Text = "OllieBoard",
            ContextMenuStrip = menu,
            Visible = true,
        };
        // A left click opens the menu too, like the menu bar item.
        tray.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(tray, null);
        };
        menu.Opening += (_, _) => Rebuild();
        Watch(menu);

        world.Start();
        foreach (var character in Character.All.Where(c => EnabledIds.Contains(c.Id))) world.SetEnabled(character, true);

        if (Updater.CanUpdate)
        {
            updater.OnInstallFailed = message =>
                MessageBox.Show(message, UI.Text(UIText.UpdateFailed), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            updater.Check();
            updateTimer.Tick += (_, _) => updater.Check();
            updateTimer.Start();
        }
    }

    protected override void ExitThreadCore()
    {
        world.Stop();
        tray.Visible = false;
        tray.Dispose();
        base.ExitThreadCore();
    }

    /// Keeps a menu (and so the whole chain) open when a checkbox inside it was clicked.
    private void Watch(ToolStripDropDown dropDown)
    {
        dropDown.Closing += (_, e) =>
        {
            if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked && keepOpen) e.Cancel = true;
        };
    }

    // Rebuilt every time it opens so warnings reflect the desktop's current state.
    private void Rebuild()
    {
        menu.Items.Clear();
        menu.Items.Add(Header(UI.Text(UIText.Characters)));
        foreach (var group in CharacterGroups(ToggleCharacter, c => world.Pets.ContainsKey(c.Id))) menu.Items.Add(group);
        menu.Items.Add(Submenu(UI.Text(UIText.PlayRoom), RoomMenu()));

        var languages = Enum.GetValues<Language>().Select(language => Item(language.Title(), () => Languages.Current = language,
            language == Languages.Current)).ToArray();
        menu.Items.Add(Submenu("Ngôn ngữ / Language / 言語", languages));

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item(UI.Text(UIText.AgentCharacters), () => world.AgentsEnabled = !world.AgentsEnabled, world.AgentsEnabled));
        if (world.AgentsEnabled)
        {
            var disabled = world.DisabledAgentKinds;
            var kinds = Enum.GetValues<AgentKind>().Select(kind => Item(kind.Title(), () =>
            {
                var set = world.DisabledAgentKinds;
                if (!set.Remove(kind)) set.Add(kind);
                world.DisabledAgentKinds = set;
            }, !disabled.Contains(kind))).ToArray();
            menu.Items.Add(Submenu("    " + UI.Text(UIText.ChooseAgents), kinds));
            foreach (var pet in world.AgentPets.Values.OrderBy(p => p.Tag ?? ""))
            {
                if (pet.Agent is not { } agent) continue;
                var state = agent.State switch
                {
                    AgentState.Idle => UI.Text(UIText.AgentIdle),
                    AgentState.Working => UI.Text(UIText.AgentWorking),
                    AgentState.Done => UI.Text(UIText.AgentDone),
                    _ => UI.Text(UIText.AgentNeedsYou),
                };
                menu.Items.Add(Header($"    {pet.Tag ?? agent.Kind.Title()} — {state}"));
            }
            menu.Items.Add(ClaudeHooks.IsInstalled
                ? Header("    " + UI.Text(UIText.HookInstalled))
                : Item("    " + UI.Text(UIText.InstallHook), InstallClaudeHooks));
        }

        if (world.Desktop.LastError is { } error)
        {
            menu.Items.Add(new ToolStripSeparator());
            var warn = Header(UI.Text(UIText.DesktopBlocked));
            warn.ToolTipText = error;
            menu.Items.Add(warn);
        }
        else if (world.Desktop.IconsArePinned)
        {
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Header(UI.Text(UIText.AutoArrangeLocked)));
            menu.Items.Add(Item(UI.Text(UIText.TurnOffAutoArrange), DisableAutoArrange));
        }
        else if (world.Desktop.IconsSnapToGrid)
        {
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Header(UI.Text(UIText.SnapToGridOn)));
            menu.Items.Add(Item(UI.Text(UIText.TurnOffSnapToGrid), DisableSnapToGrid));
        }

        menu.Items.Add(new ToolStripSeparator());
        if (Updater.CanUpdate) menu.Items.Add(UpdateItem());
        menu.Items.Add(Item(UI.Text(UIText.Quit), Application.Exit));
    }

    /// One item that shows where the update check is and, when a newer version is out, installs it.
    private ToolStripItem UpdateItem()
    {
        var current = Updater.CurrentVersion;
        switch (updater.Current)
        {
            case Updater.State.Available available:
                return Item(UI.Text(UIText.UpdateAvailable, available.Version), updater.Install);
            case Updater.State.Installing:
                return Header(UI.Text(UIText.InstallingUpdate));
            case Updater.State.Checking:
                return Header(UI.Text(UIText.CheckingUpdates));
            case Updater.State.UpToDate:
                return Item(UI.Text(UIText.UpToDate, current), updater.Check);
            case Updater.State.Failed failed:
                var item = Item(UI.Text(UIText.CheckUpdates, current), updater.Check);
                item.ToolTipText = failed.Message;
                return item;
            default:
                return Item(UI.Text(UIText.CheckUpdates, current), updater.Check);
        }
    }

    private ToolStripItem[] RoomMenu()
    {
        var room = world.Room;
        var scenes = Enum.GetValues<RoomScene>().Select(scene => Item(scene.Title(), () => room.SetScene(scene), scene == room.Scene)).ToArray();
        return
        [
            Item(UI.Text(UIText.ShowRoom), () => room.SetOpen(!room.IsOpen), room.IsOpen),
            Submenu(UI.Text(UIText.Scenery), scenes),
            new ToolStripSeparator(),
            Header(UI.Text(UIText.InRoom)),
            .. CharacterGroups(ToggleRoomMember, c => room.Members.Contains(c.Id)),
            new ToolStripSeparator(),
            Item(UI.Text(UIText.EditRoom), () => room.SetEditing(!room.Editing), room.Editing),
        ];
    }

    /// One submenu per series listing its characters as checkboxes, so several can be toggled without the menu
    /// closing. A series shows a dot when some of its characters are on.
    private List<ToolStripMenuItem> CharacterGroups(Action<Character> toggle, Func<Character, bool> isOn)
    {
        var groups = new List<ToolStripMenuItem>();
        foreach (var series in Enum.GetValues<Series>())
        {
            var members = Character.All.Where(c => c.Series == series).ToList();
            if (members.Count == 0) continue;
            var group = new ToolStripMenuItem(series.Title());
            foreach (var character in members)
            {
                var box = new ToolStripMenuItem(character.Name) { Checked = isOn(character) };
                box.Click += (_, _) =>
                {
                    keepOpen = true;
                    toggle(character);
                    box.Checked = isOn(character);
                    UpdateGroup(group);
                    menu.BeginInvoke(() => keepOpen = false);
                };
                group.DropDownItems.Add(box);
            }
            Watch(group.DropDown);
            UpdateGroup(group);
            groups.Add(group);
        }
        return groups;
    }

    private static void UpdateGroup(ToolStripMenuItem group) =>
        group.CheckState = group.DropDownItems.OfType<ToolStripMenuItem>().Any(i => i.Checked) ? CheckState.Indeterminate : CheckState.Unchecked;

    private static ToolStripMenuItem Header(string title) => new(title) { Enabled = false };

    private static ToolStripMenuItem Item(string title, Action action, bool isChecked = false)
    {
        var item = new ToolStripMenuItem(title) { Checked = isChecked };
        item.Click += (_, _) => action();
        return item;
    }

    private ToolStripMenuItem Submenu(string title, ToolStripItem[] items)
    {
        var item = new ToolStripMenuItem(title);
        item.DropDownItems.AddRange(items);
        Watch(item.DropDown);
        return item;
    }

    private void ToggleCharacter(Character character)
    {
        var enable = !world.Pets.ContainsKey(character.Id);
        world.SetEnabled(character, enable);
        var ids = EnabledIds;
        if (enable) ids.Add(character.Id); else ids.Remove(character.Id);
        EnabledIds = ids;
    }

    /// Putting a character in the room also brings it out if it was turned off.
    private void ToggleRoomMember(Character character)
    {
        var inside = !world.Room.Members.Contains(character.Id);
        world.Room.SetMember(character.Id, inside);
        if (inside && !world.Pets.ContainsKey(character.Id))
        {
            world.SetEnabled(character, true);
            var ids = EnabledIds;
            ids.Add(character.Id);
            EnabledIds = ids;
        }
    }

    private void InstallClaudeHooks()
    {
        var answer = MessageBox.Show(UI.Text(UIText.HookExplain, ClaudeHooks.SettingsPath), UI.Text(UIText.HookQuestion),
            MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (answer != DialogResult.OK) return;
        try
        {
            ClaudeHooks.Install();
            MessageBox.Show(UI.Text(UIText.HookDoneInfo), UI.Text(UIText.HookDone), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception e)
        {
            MessageBox.Show(e.Message, UI.Text(UIText.HookFailed), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void DisableAutoArrange() => world.Desktop.DisableAutoArrange(ok =>
    {
        if (!ok) MessageBox.Show(UI.Text(UIText.AutoArrangeHelp), "OllieBoard", MessageBoxButtons.OK, MessageBoxIcon.Information);
    });

    private void DisableSnapToGrid() => world.Desktop.DisableSnapToGrid(ok =>
    {
        if (!ok) MessageBox.Show(UI.Text(UIText.SnapToGridHelp), "OllieBoard", MessageBoxButtons.OK, MessageBoxIcon.Information);
    });
}
