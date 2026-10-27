namespace OllieBoard;

/// Owns the pets, drives the animation clock and coordinates access to desktop icons.
sealed class World
{
    public readonly DesktopIcons Desktop = new();
    public readonly AgentMonitor Agents = new();
    public readonly Room Room = new();
    private readonly Dictionary<string, Pet> pets = new();
    public IReadOnlyDictionary<string, Pet> Pets => pets;
    /// Mascots of running AI agent sessions, by agent process id.
    private readonly Dictionary<int, Pet> agentPets = new();
    public IReadOnlyDictionary<int, Pet> AgentPets => agentPets;
    public Pet? Arranger;
    /// Icons the last cleaner put in place, so kicking one of them upsets that pet.
    public HashSet<string> Arranged = new();
    public Pet? ArrangedBy;
    private readonly HashSet<string> claimed = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 / 30 };
    private double lastTick = Clock.Now;
    private double lastRefresh;
    private double lastPin;

    private const string AgentsKey = "agentCharacters";
    private const string DisabledKindsKey = "disabledAgentKinds";

    /// Whether running AI agents get their own characters.
    public bool AgentsEnabled
    {
        get => Settings.GetBool(AgentsKey, true);
        set
        {
            Settings.SetBool(AgentsKey, value);
            if (value) Agents.Start(); else Agents.Stop();
        }
    }

    /// Agent kinds switched off in the menu; their sessions get no character.
    public HashSet<AgentKind> DisabledAgentKinds
    {
        get => (Settings.GetStringList(DisabledKindsKey) ?? [])
            .Select(s => Enum.TryParse<AgentKind>(s, true, out var k) ? (AgentKind?)k : null).OfType<AgentKind>().ToHashSet();
        set => Settings.SetStringList(DisabledKindsKey, value.Select(k => k.Key()));
    }

    public void Start()
    {
        Desktop.Refresh();
        Agents.OnUpdate = SyncAgents;
        if (AgentsEnabled) Agents.Start();
        Room.OnMembersChange = () => { foreach (var pet in pets.Values.ToList()) pet.MoveIntoRoom(); };
        Room.OnMove = (dx, dy) => { foreach (var pet in pets.Values) pet.FollowRoom(dx, dy); };
        Room.Start();
        timer.Tick += (_, _) => Tick();
        timer.Start();
    }

    public void Stop()
    {
        timer.Stop();
        Agents.Stop();
        foreach (var pet in pets.Values) pet.Hide();
    }

    private void Tick()
    {
        var now = Clock.Now;
        var dt = Math.Min(now - lastTick, 0.1);
        lastTick = now;
        foreach (var pet in pets.Values.ToList()) pet.Tick(dt);
        if (now - lastRefresh > 15)
        {
            lastRefresh = now;
            Desktop.Refresh();
        }
        // Keep the room just above the desktop and its members just above the room.
        if (now - lastPin > 1)
        {
            lastPin = now;
            Room.Pin(pets.Values.Where(p => p.InRoom && !p.OnTop).Select(p => p.Handle));
        }
    }

    // MARK: Pets

    public void SetEnabled(Character character, bool enabled)
    {
        if (enabled && !pets.ContainsKey(character.Id))
        {
            var index = Array.FindIndex(Character.All, c => c.Id == character.Id);
            var f = Screens.Main.Visible;
            var x = f.MinX + 140 + Math.Max(index, 0) * 170;
            var pet = new Pet(character, this, new PointD(Math.Min(x, f.MaxX - 60), f.MinY + 10));
            pets[character.Id] = pet;
            pet.Show();
            pet.MoveIntoRoom();
        }
        else if (!enabled && pets.Remove(character.Id, out var pet))
        {
            Forget(pet);
            pet.Close();
        }
    }

    private void SyncAgents(Dictionary<int, AgentSession> all)
    {
        var disabled = DisabledAgentKinds;
        var sessions = all.Where(s => !disabled.Contains(s.Value.Kind)).ToDictionary(s => s.Key, s => s.Value);
        foreach (var (pid, session) in sessions)
        {
            if (agentPets.TryGetValue(pid, out var existing))
            {
                existing.Update(session);
                continue;
            }
            // Starts hidden; the mascot shows itself once the agent works or waits for the user.
            var character = Character.ForAgent(session.Kind, pid);
            var pet = new Pet(character, this, PointD.Zero, session);
            pets[character.Id] = pet;
            agentPets[pid] = pet;
            pet.Update(session);
        }
        foreach (var (pid, pet) in agentPets.Where(p => !sessions.ContainsKey(p.Key)).ToList())
        {
            agentPets.Remove(pid);
            // Closed after the tick that runs this, since it is the pet's own last step.
            pet.Leave(() => Dispatch.Async(() =>
            {
                pets.Remove(pet.Character.Id);
                Forget(pet);
                pet.Close();
            }));
        }
    }

    private void Forget(Pet pet)
    {
        if (Arranger == pet) Arranger = null;
        if (ArrangedBy == pet) ArrangedBy = null;
    }

    /// Sometimes a friend (on the same side of the room walls) walks over to cheer up an upset (shy) pet.
    public void RequestComfort(Pet pet)
    {
        if (Random.Shared.NextDouble() >= 0.6) return;
        var helper = pets.Values.Where(p => p != pet && p.IsAvailable && !p.Character.Shy && p.InRoom == pet.InRoom).ToList().RandomElement();
        helper?.Comfort(pet);
    }

    // MARK: Icons

    public void Claim(string name) => claimed.Add(name);
    public void Release(string name) => claimed.Remove(name);

    public List<DesktopIcon> AvailableIcons() => Desktop.Icons.Where(i => !claimed.Contains(i.Name)).ToList();

    /// A random icon among the few closest to `point`, so pets don't cross every screen for one kick.
    public DesktopIcon? PickIcon(PointD point)
    {
        var sorted = AvailableIcons().OrderBy(i => Mathx.Distance(i.Center, point)).Take(5).ToList();
        return sorted.Count == 0 ? null : sorted[Random.Shared.Next(sorted.Count)];
    }

    /// Neat grid in the top-right corner of the primary screen, filled column by column like Explorer does.
    public List<PointD> GridSlots(int count)
    {
        var f = Screens.Main.Visible;
        const double spacing = 100;
        var rows = Math.Max(1, (int)((f.Height - 70) / spacing));
        return Enumerable.Range(0, count)
            .Select(i => new PointD(f.MaxX - 70 - (i / rows) * spacing, f.MaxY - 70 - (i % rows) * spacing)).ToList();
    }
}
