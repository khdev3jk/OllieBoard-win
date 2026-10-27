namespace OllieBoard;

/// One character on screen: a queue of steps (walk, play an animation, run an action)
/// refilled by a weighted choice of behaviors whenever it runs dry.
sealed class Pet
{
    abstract record Step;
    sealed record WalkStep(PointD Goal) : Step;
    /// Drops straight down to this height (a pet let go above the room's floor).
    sealed record FallStep(double Y) : Step;
    sealed record PlayStep(Anim Anim, double Duration, Effect? Effect = null, Line? Say = null) : Step;
    /// Plays an animation once from its start and stays on its last frame (Bocchi's puddle);
    /// if it is already playing, it carries on from where it is instead of restarting.
    sealed record HoldStep(Anim Anim, double Duration) : Step;
    sealed record FaceStep(double Facing) : Step;
    sealed record RunStep(Action Action) : Step;
    sealed record LineStep(Line Line, Character? To) : Step;

    private static Step Walk(PointD p) => new WalkStep(p);
    private static Step Play(Anim a, double d, Effect? effect = null, Line? say = null) => new PlayStep(a, d, effect, say);
    private static Step Hold(Anim a, double d) => new HoldStep(a, d);
    private static Step Face(double f) => new FaceStep(f);
    private static Step Run(Action a) => new RunStep(a);
    private static Step Say(Line l, Character? to = null) => new LineStep(l, to);

    /// Things other pets do to this one.
    enum Social
    {
        Invite, Greet, Woken, Hit, Pranked, Chased, Comfort,
        /// Special moves: Anya reading this pet's mind, Bocchi's guitar solo.
        MindRead, Listen,
        /// Special moves: Putin's judo throw, Kim's applause.
        Tossed, Applaud,
        /// Special move: caught in Gojo's Infinite Void, frozen in place.
        Stunned,
    }

    /// How high above the feet a carried icon is held.
    private const double CarryLift = 132;

    public readonly Character Character;
    private readonly SpriteSheet sheet;
    private readonly World world;
    private readonly PetWindow window;

    public PointD Position { get; private set; }
    public double Facing { get; private set; } = 1;

    private Anim anim = Anim.Idle;
    private double animStart;
    private (Effect Effect, double Start, double Until)? effect;
    private (string Text, double Until, bool Thought)? bubble;
    private bool holding;
    /// Nezuko's size change: the drawn scale, the size it is heading to and when that wears off.
    private double scale = 1;
    private double targetScale = 1;
    private double resizedUntil;

    private readonly List<Step> steps = new();
    private Step? current;
    private double stepStart;

    public string? Carrying { get; private set; }
    /// Icon picked for a kick that hasn't flown yet, so it can be freed if the kick is called off.
    private string? kickTarget;
    private bool dragging;
    private double boredom;
    private List<double> clicks = new();
    /// Busy with a kick, cleanup or visit; other pets won't pull it into a social interaction.
    private bool engaged;

    /// Set for the mascot of a running AI agent session.
    public AgentSession? Agent { get; private set; }
    private bool leaving;
    /// Turned off or gone: its window is closed and delayed actions must not touch it any more.
    private bool closed;
    private double lastAgentCall;
    private int agentReminders;
    /// Agent mascots are only on screen while their agent works or waits for the user.
    private bool visible;
    private AgentState? handledState;
    private bool greeted;

    public bool IsSleeping => anim == Anim.Sleep;
    /// Free to be pulled into games with other characters (agents never are).
    public bool IsAvailable => Agent == null && !dragging && !engaged && !leaving && Carrying == null && !IsSleeping;

    /// Name tag shown under agent mascots, e.g. "Claude · OllieBoard".
    public string? Tag => Agent == null ? null
        : Agent.Project.Length == 0 ? Agent.Kind.Title() : $"{Agent.Kind.Title()} · {Agent.Project}";

    public IntPtr Handle => window.Handle;
    public bool OnTop => window.Topmost;

    private static double Now => Clock.Now;

    public Pet(Character character, World world, PointD position, AgentSession? agent = null)
    {
        Character = character;
        sheet = agent != null ? SpriteSheet.Named(agent.Kind.Key(), skills: false) : SpriteSheet.Named(character.Id);
        this.world = world;
        Position = position;
        Agent = agent;
        window = new PetWindow(this);
        window.EnsureHandle();
        Facing = Mathx.Coin() ? 1 : -1;
    }

    public void Show()
    {
        window.Place(Position);
        window.OrderFront();
        steps.Clear();
        steps.Add(Play(Anim.Happy, 1.4, Effect.Heart, Line.Happy));
    }

    public void Hide()
    {
        if (Carrying is { } name) Drop(name, CarryPoint);
        if (world.Arranger == this) world.Arranger = null;
        window.OrderOut();
    }

    /// Closes the window for good (the pet was turned off or its agent session ended).
    public void Close()
    {
        closed = true;
        Hide();
        window.Close();
        window.Dispose();
    }

    // MARK: - Rendering state

    private Bitmap? CurrentFrame()
    {
        var elapsed = Now - animStart;
        return sheet.Frame(anim, holding ? Math.Min(elapsed, sheet.Duration(anim) - 0.01) : elapsed);
    }

    private (Effect, double)? CurrentEffect() =>
        effect is { } e && Now < e.Until ? (e.Effect, Now - e.Start) : null;

    private (string Text, bool Thought)? CurrentBubble() =>
        bubble is { } b && Now < b.Until ? (b.Text, b.Thought) : null;

    private void SetAnim(Anim a)
    {
        if (a != anim) { anim = a; animStart = Now; }
    }

    private void SayLine(Line line, Character? partner = null, string app = "", bool thought = false)
    {
        if (Character.Say(line, partner, Agent?.Project ?? "", app) is { } text) bubble = (text, Now + 2.8, thought);
    }

    // MARK: - Tick

    public void Tick(double dt)
    {
        if (closed || Agent != null && !visible) return;
        if (Agent is { } agent && agent.State is AgentState.Done or AgentState.NeedsInput
            && agentReminders < 2 && Now - lastAgentCall > 60 && IsAvailable)
        {
            agentReminders += 1;
            CallUser();
        }
        if (dragging) SetAnim(Anim.Dragged);
        else Advance(dt);
        if (closed) return;   // the last step closed it (an agent mascot walking off)
        if (anim is Anim.Idle or Anim.Walk or Anim.Bored) boredom += dt;
        if (targetScale != 1 && Now > resizedUntil)
        {
            targetScale = 1;
            window.Puff();
        }
        if (scale != targetScale)
        {
            scale += (targetScale - scale) * Math.Min(1, dt * 8);
            if (Math.Abs(targetScale - scale) < 0.01) scale = targetScale;
        }
        window.Place(Position);
        window.Topmost = !InRoom;
        UpdateMousePassthrough();
        var fx = CurrentEffect();
        var b = CurrentBubble();
        window.Render(CurrentFrame(), Facing, scale, fx is { } f ? SpriteSheet.Effect(f.Item1, (int)Math.Round(52 * Screens.Scale)) : null,
            fx?.Item2 ?? 0, b?.Text, b?.Thought ?? false, Tag);
    }

    private void Advance(double dt)
    {
        if (current == null) StartNextStep();
        switch (current)
        {
            case WalkStep walk:
            {
                var target = Confined(walk.Goal);
                var dx = target.X - Position.X;
                var dy = target.Y - Position.Y;
                var dist = Mathx.Hypot(dx, dy);
                var fast = Carrying == null && dist > 450;
                var speed = Character.WalkSpeed * (Carrying != null ? 0.75 : fast ? 2.2 : 1);
                SetAnim(Carrying != null ? Anim.Carry : fast ? Anim.Run : Anim.Walk);
                if (Math.Abs(dx) > 1) Facing = dx > 0 ? 1 : -1;
                var len = speed * dt;
                if (dist <= len)
                {
                    Position = target;
                    current = null;
                }
                else
                {
                    Position = new PointD(Position.X + dx / dist * len, Position.Y + dy / dist * len);
                }
                if (Carrying is { } name) world.Desktop.MoveLive(name, CarryPoint);
                break;
            }
            case FallStep fall:
                SetAnim(Anim.Dragged);
                Position = Position with { Y = Math.Max(fall.Y, Position.Y - 700 * dt) };
                if (Position.Y <= fall.Y) current = null;
                break;
            case PlayStep play:
                if (Now - stepStart >= play.Duration) current = null;
                break;
            case HoldStep hold:
                if (Now - stepStart >= hold.Duration) current = null;
                break;
            case FaceStep or RunStep or LineStep:
                current = null;
                break;
        }
    }

    private void StartNextStep()
    {
        if (steps.Count == 0)
        {
            if (Agent is { } agent)
            {
                // Mascots type on their laptop while the agent is busy, and wait near you once it's done.
                steps.Add(agent.State == AgentState.Working ? Play(Anim.Work, 5, Effect.Code) : Play(Anim.Idle, 4));
            }
            else
            {
                steps.AddRange(Plan(ChooseBehavior()));
            }
        }
        if (steps.Count == 0) return;
        var step = steps[0];
        steps.RemoveAt(0);
        current = step;
        stepStart = Now;
        holding = false;
        switch (step)
        {
            case PlayStep play:
                SetAnim(play.Anim);
                if (play.Effect is { } fx) effect = (fx, Now, Now + play.Duration);
                if (play.Say is { } line) SayLine(line);
                break;
            case FaceStep face:
                Facing = face.Facing;
                break;
            case RunStep run:
                run.Action();
                break;
            case LineStep say:
                SayLine(say.Line, say.To);
                break;
            case HoldStep hold:
                SetAnim(hold.Anim);
                holding = true;
                break;
        }
    }

    /// Plays `newSteps` right away, then resumes what the pet was doing (an interrupted walk or fall is redone).
    private void Interrupt(IEnumerable<Step> newSteps)
    {
        if (current is WalkStep or FallStep) steps.Insert(0, current);
        steps.InsertRange(0, newSteps);
        current = null;
    }

    // MARK: - Behaviors

    private Behavior ChooseBehavior()
    {
        var hasIcons = world.AvailableIcons().Count > 0;
        var weights = new Dictionary<Behavior, double>(Character.Weights);
        double W(Behavior b) => weights.GetValueOrDefault(b);
        weights[Behavior.Bored] = W(Behavior.Bored) * (1 + boredom / 40);
        weights[Behavior.Sleep] = W(Behavior.Sleep) * (1 + boredom / 60);
        weights[Behavior.Kick] = W(Behavior.Kick) * (InRoom ? 0 : hasIcons ? 1 : 0.1);
        weights[Behavior.Arrange] = W(Behavior.Arrange) * (!InRoom && hasIcons && world.Arranger == null ? 1 : 0);
        weights[Behavior.Social] = W(Behavior.Social) *
            (world.Pets.Values.Any(p => p != this && p.Agent == null && p.InRoom == InRoom) ? 1 : 0);
        var total = weights.Values.Sum();
        var roll = Random.Shared.NextDouble() * Math.Max(total, 0.001);
        foreach (var behavior in Enum.GetValues<Behavior>())
        {
            roll -= W(behavior);
            if (roll < 0) return behavior;
        }
        return Behavior.Wander;
    }

    private List<Step> Plan(Behavior behavior)
    {
        if (Character.Shy && FleeFromCursor() is { } flee) return flee;
        switch (behavior)
        {
            case Behavior.Wander:
                return [Walk(RandomDestination()), Play(Anim.Idle, Mathx.Random(1, 3))];
            case Behavior.Kick:
                return KickPlan();
            case Behavior.Arrange:
                return ArrangePlan();
            case Behavior.Bored:
                // Join a friend who is already bored instead of sulking alone.
                if (world.Pets.Values.FirstOrDefault(p => p != this && p.anim == Anim.Bored && p.InRoom == InRoom) is { } buddy)
                {
                    return [Walk(Beside(buddy)),
                            Face(buddy.Position.X > Position.X ? 1 : -1),
                            Say(Line.Together, buddy.Character),
                            Play(Anim.Bored, Mathx.Random(5, 9), Effect.Dots)];
                }
                return [Play(Anim.Bored, Mathx.Random(4, 8), Effect.Dots, Line.Bored)];
            case Behavior.Sleep:
                return [Play(Anim.Yawn, sheet.Duration(Anim.Yawn) + 0.3, say: Line.Tedious),
                        Play(Anim.Sleep, Mathx.Random(8, 16), Effect.Zzz),
                        Run(() => boredom = 0)];
            case Behavior.Grumpy:
                return [Play(Character.GrumpyAnim, 2.5, Effect.Anger, Line.Grumpy), AskForComfort()];
            case Behavior.Happy:
                return [Play(Anim.Happy, 1.8, Effect.Note, Line.Happy),
                        Run(() => boredom = Math.Max(0, boredom - 20))];
            case Behavior.Social:
                return SocialPlan();
            default:
                return Character.Skills.Length == 0 ? Plan(Behavior.Wander) : SkillPlan(Character.Skills.RandomElement());
        }
    }

    /// Wraps a multi-step task so other pets leave this one alone until it finishes.
    private List<Step> Engage(IEnumerable<Step> plan) =>
        [Run(() => engaged = true), .. plan, Run(() => engaged = false)];

    private List<Step>? FleeFromCursor()
    {
        var m = Screens.Mouse;
        var dx = Position.X - m.X;
        var dy = Position.Y - m.Y;
        var d = Mathx.Hypot(dx, dy);
        if (d >= 170 || d <= 0) return null;
        var target = ClampToScreens(new PointD(Position.X + dx / d * 260, Position.Y + dy / d * 260));
        return [Play(Anim.Surprised, 0.4, Effect.Exclaim),
                Walk(target),
                Play(Character.GrumpyAnim, 1.5, Effect.Sweat, Line.Grumpy),
                AskForComfort()];
    }

    // MARK: Kicking icons

    private List<Step> KickPlan()
    {
        if (world.PickIcon(Position) is not { } icon)
            return [Play(Character.GrumpyAnim, 2, Effect.Dots, Line.NoIcons)];
        world.Claim(icon.Name);
        kickTarget = icon.Name;
        double side = icon.Center.X >= Position.X ? 1 : -1;
        var stand = ClampToScreens(new PointD(icon.Center.X - side * 46, icon.Center.Y - 44));
        return Engage([Walk(stand),
                       Face(side),
                       Play(Anim.Kick, 0.33, say: Line.Kick),
                       Run(() => Launch(icon.Name, side)),
                       Play(Anim.Kick, 0.3),
                       Play(Anim.Happy, 1.2, Effect.Sparkle)]);
    }

    private void Launch(string name, double side)
    {
        kickTarget = null;
        if (world.Desktop.Icons.FirstOrDefault(i => i.Name == name) is not { Name: not null } icon)
        {
            world.Release(name);
            return;
        }
        var path = FlightPath(icon.Center, side, 0, Mathx.Random(160, 320));
        // Friends standing in the flight path get hit.
        foreach (var other in world.Pets.Values.Where(o => o != this && !o.InRoom).ToList())
        {
            var body = new PointD(other.Position.X, other.Position.Y + 55);
            var i = path.FindIndex(p => Mathx.Distance(p, body) < 55);
            if (i >= 0) Dispatch.After(0.15 + i * 0.03, () => other.Receive(Social.Hit, this));
        }
        // Kicking an icon someone just tidied up starts a chase.
        if (world.ArrangedBy is { } owner && owner != this && !owner.InRoom && world.Arranged.Remove(name))
            Dispatch.After(0.6, () => owner.Receive(Social.Pranked, this));
        world.Desktop.Move(name, path, completion: final =>
        {
            world.Release(name);
            if (final == null || Mathx.Distance(final.Value, icon.Center) < 10)
                Interrupt([Play(Character.GrumpyAnim, 2, Effect.Sweat, Line.Fail)]);
        });
    }

    /// A kicked icon's arc: `distance` along the direction (dx, dy) with a hop, kept on the icon's screen.
    private List<PointD> FlightPath(PointD start, double dx, double dy, double distance)
    {
        var bounds = ScreenFrame(start).Inset(40, 40);
        const int n = 12;
        var path = new List<PointD>();
        for (var i = 1; i <= n; i++)
        {
            var t = (double)i / n;
            var ease = 1 - (1 - t) * (1 - t);
            var x = start.X + dx * distance * ease;
            var y = start.Y + dy * distance * ease + Math.Sin(t * Math.PI) * 50;
            path.Add(new PointD(Mathx.Clamp(x, bounds.MinX, bounds.MaxX), Mathx.Clamp(y, bounds.MinY, bounds.MaxY)));
        }
        return path;
    }

    // MARK: Arranging icons

    private PointD CarryPoint => new(Position.X, Position.Y + CarryLift);

    private PointD FeetUnder(PointD p)
    {
        var bottom = ScreenFrame(p).MinY;
        return new PointD(p.X, Math.Max(bottom, p.Y - CarryLift));
    }

    private List<Step> ArrangePlan()
    {
        if (world.Arranger != null) return Plan(Behavior.Wander);
        var icons = world.AvailableIcons().OrderBy(i => i.Name, NaturalComparer.Instance).ToList();
        if (icons.Count == 0) return [Play(Character.GrumpyAnim, 2, Effect.Dots, Line.NoIcons)];
        var slots = world.GridSlots(icons.Count);

        var work = new List<Step>();
        foreach (var (icon, slot) in icons.Zip(slots).Where(p => Mathx.Distance(p.First.Center, p.Second) > 12).Take(10))
        {
            work.AddRange([Walk(FeetUnder(icon.Center)),
                           Run(() => PickUp(icon.Name)),
                           Walk(FeetUnder(slot)),
                           Run(() => Drop(icon.Name, slot)),
                           Play(Anim.Idle, 0.25)]);
        }
        if (work.Count == 0) return [Play(Anim.Happy, 1.5, Effect.Sparkle, Line.Done)];
        world.Arranger = this;
        return Engage([Play(Anim.Idle, 0.8, Effect.Exclaim, Line.Arrange), .. work,
                       Run(() =>
                       {
                           if (world.Arranger == this) world.Arranger = null;
                           world.Arranged = icons.Select(i => i.Name).ToHashSet();
                           world.ArrangedBy = this;
                       }),
                       Play(Anim.Happy, 2, Effect.Sparkle, Line.Done)]);
    }

    private void PickUp(string name)
    {
        world.Claim(name);
        Carrying = name;
    }

    private void Drop(string name, PointD point)
    {
        Carrying = null;
        world.Desktop.CancelLive(name);
        world.Desktop.Move(name, [point], completion: _ => world.Release(name));
    }

    // MARK: - Agent

    public void Update(AgentSession session)
    {
        Agent = session;
        if (leaving || session.State == handledState) return;
        handledState = session.State;
        if (session.State == AgentState.Idle) { Disappear(); return; }
        var justAppeared = !visible;
        if (justAppeared) Appear();
        switch (session.State)
        {
            case AgentState.Working:
                // Start working as soon as the current errand allows.
                if (!engaged && !dragging)
                {
                    steps.Clear();
                    current = null;
                }
                SayLine(greeted ? Line.AgentWorking : Line.AgentHello);
                greeted = true;
                break;
            case AgentState.Done or AgentState.NeedsInput:
                if (justAppeared)
                {
                    // Skip the stroll to a spot and head straight for the user.
                    steps.Clear();
                    engaged = false;
                }
                agentReminders = 0;
                CallUser();
                break;
        }
    }

    /// Walks in from the nearest edge of the main screen to a spot along the bottom.
    private void Appear()
    {
        var f = ScreenFrame(Screens.Mouse);
        var spot = new PointD(Mathx.Random(f.MinX + 80, f.MaxX - 80), f.MinY + Mathx.Random(10, 80));
        Position = new PointD(spot.X < f.MidX ? f.MinX - 80 : f.MaxX + 80, spot.Y);
        visible = true;
        // Engaged right away so the state handling that follows doesn't cut the entrance short.
        engaged = true;
        steps.Clear();
        steps.AddRange([Walk(spot), Run(() => engaged = false)]);
        current = null;
        window.Place(Position);
        window.OrderFront();
    }

    private void Disappear()
    {
        visible = false;
        steps.Clear();
        current = null;
        engaged = false;
        bubble = null;
        effect = null;
        window.OrderOut();
    }

    /// Runs over to the mouse cursor to say the agent finished or needs the user.
    private void CallUser()
    {
        if (Agent is not { } agent) return;
        lastAgentCall = Now;
        var line = agent.State == AgentState.NeedsInput ? Line.AgentNeedsYou : Line.AgentDone;
        var m = Screens.Mouse;
        double side = Position.X < m.X ? -1 : 1;
        Interrupt(Engage([Play(Anim.Surprised, 0.4, Effect.Exclaim),
                          Walk(ClampToScreens(new PointD(m.X + side * 70, m.Y - 110))),
                          Face(-side),
                          Say(line),
                          Play(Anim.Happy, 2.2, line == Line.AgentDone ? Effect.Check : Effect.Exclaim)]));
    }

    /// The agent session ended: wave goodbye and walk off the nearest screen edge.
    public void Leave(Action completion)
    {
        if (leaving) return;
        leaving = true;
        if (!(visible || Agent == null)) { completion(); return; }
        if (Carrying is { } name) Drop(name, CarryPoint);
        if (world.Arranger == this) world.Arranger = null;
        var f = ScreenFrame(Position);
        var exitX = Position.X - f.MinX < f.MaxX - Position.X ? f.MinX - 80 : f.MaxX + 80;
        steps.Clear();
        steps.AddRange([Say(Line.AgentBye),
                        Play(Anim.Happy, 1.2, Effect.Heart),
                        Walk(new PointD(exitX, Position.Y)),
                        Run(completion)]);
        current = null;
    }

    // MARK: - Friends

    private double DirectionTo(Pet other) => other.Position.X >= Position.X ? 1 : -1;

    /// A spot right next to `other`, on the side this pet is coming from.
    private PointD Beside(Pet other) =>
        ClampToScreens(new PointD(other.Position.X + (Position.X < other.Position.X ? -60 : 60), other.Position.Y));

    private List<Step> SocialPlan()
    {
        var others = world.Pets.Values.Where(p => p != this && p.InRoom == InRoom).ToList();
        // Pranksters (and sometimes the others) wake up sleeping friends.
        if (others.Where(p => p.IsSleeping).ToList().RandomElement() is { } sleeper
            && (Character.Prankster || Random.Shared.NextDouble() < 0.3))
        {
            return Engage([Walk(Beside(sleeper)),
                           Face(DirectionTo(sleeper)),
                           Say(Line.WakeOther, sleeper.Character),
                           Play(Anim.Happy, 0.6, Effect.Exclaim),
                           Run(() => sleeper.Receive(Social.Woken, this)),
                           Play(Anim.Happy, 1.4, Effect.Note)]);
        }
        // Only friends close enough to reach before they get tired of waiting.
        var friend = others.Where(p => p.IsAvailable && Mathx.Distance(p.Position, Position) < 900)
            .MinBy(p => Mathx.Distance(p.Position, Position));
        if (friend == null) return Plan(Behavior.Wander);
        friend.Receive(Social.Invite, this);
        return Engage([Walk(Beside(friend)),
                       Face(DirectionTo(friend)),
                       Say(Line.Greet, friend.Character),
                       Play(Anim.Happy, 1.4, Effect.Heart),
                       Run(() => friend.Receive(Social.Greet, this)),
                       Play(Anim.Idle, 1.2)]);
    }

    /// Shy characters hope someone notices they are upset.
    private Step AskForComfort() => Run(() =>
    {
        if (Character.Shy) world.RequestComfort(this);
    });

    public void Comfort(Pet friend)
    {
        friend.Receive(Social.Invite, this);
        Interrupt(Engage([Walk(Beside(friend)),
                          Face(DirectionTo(friend)),
                          Say(Line.Comfort, friend.Character),
                          Play(Anim.Idle, 1.0, Effect.Heart),
                          Run(() => friend.Receive(Social.Comfort, this)),
                          Play(Anim.Happy, 1.2)]));
    }

    private void Receive(Social social, Pet other)
    {
        if (closed || Agent != null || dragging || leaving) return;
        switch (social)
        {
            case Social.Invite:
                if (!IsAvailable) return;
                // Wait for the visitor to arrive.
                Interrupt(Engage([Face(DirectionTo(other)), Play(Anim.Idle, 6)]));
                break;
            case Social.Greet:
                List<Step> flustered = Character.Shy ? [Play(Anim.Surprised, 0.5, Effect.Exclaim)] : [];
                Interrupt([.. flustered,
                           Face(DirectionTo(other)),
                           Say(Line.GreetBack, other.Character),
                           Play(Anim.Happy, 1.6, Effect.Heart)]);
                boredom = 0;
                break;
            case Social.Woken:
                Interrupt([Play(Anim.Surprised, 0.7, Effect.Exclaim),
                           Face(DirectionTo(other)),
                           Play(Character.GrumpyAnim, 1.8, Effect.Anger, Line.Wake),
                           AskForComfort()]);
                break;
            case Social.Hit:
                Interrupt([Play(Anim.Surprised, 0.5, Effect.Exclaim),
                           Face(DirectionTo(other)),
                           Say(Line.Hit, other.Character),
                           Play(Character.GrumpyAnim, 1.8, Effect.Anger),
                           Run(() => other.Interrupt([Face(other.DirectionTo(this)),
                                                      Say(Line.Sorry, Character),
                                                      Play(other.Character.Prankster ? Anim.Happy : Anim.Sad, 1.5, Effect.Sweat)])),
                           AskForComfort()]);
                break;
            case Social.Pranked:
                Interrupt(Engage([Play(Anim.Surprised, 0.5, Effect.Exclaim),
                                  Face(DirectionTo(other)),
                                  Say(Line.Pranked, other.Character),
                                  Play(Character.GrumpyAnim, 1.4, Effect.Anger),
                                  Run(() => other.Receive(Social.Chased, this)),
                                  Play(Anim.Idle, 0.6),
                                  // Chase toward wherever the culprit has run off to by now.
                                  Run(() => steps.Insert(0, Walk(Beside(other)))),
                                  Play(Character.GrumpyAnim, 1.5, Effect.Anger, Line.Grumpy)]));
                break;
            case Social.Chased:
                var away = -DirectionTo(other);
                Interrupt([Face(away),
                           Say(Line.Chased, other.Character),
                           Walk(ClampToScreens(new PointD(Position.X + away * 300, Position.Y))),
                           Play(Anim.Happy, 1, Effect.Note)]);
                break;
            case Social.Comfort:
                boredom = 0;
                Interrupt([Face(DirectionTo(other)),
                           Say(Line.Thanks, other.Character),
                           Play(Anim.Happy, 1.8, Effect.Heart)]);
                break;
            case Social.MindRead:
                // The secret thought pops up for everyone to see.
                Interrupt([Face(DirectionTo(other)),
                           Run(() => SayLine(Line.Thought, thought: true)),
                           Play(Anim.Idle, 2.4),
                           Play(Anim.Surprised, 0.6, Effect.Exclaim),
                           Play(Character.GrumpyAnim, 1.2, Effect.Sweat)]);
                break;
            case Social.Listen:
            {
                if (!IsAvailable) return;
                double side = Position.X < other.Position.X ? -1 : 1;
                var seat = ClampToScreens(new PointD(other.Position.X + side * 100, other.Position.Y + Mathx.Random(-14, 14)));
                Interrupt(Engage([Walk(seat),
                                  Face(-side),
                                  Play(Anim.Happy, 3.5, Effect.Note),
                                  Say(Line.Cheer, other.Character),
                                  Play(Anim.Happy, 1.4, Effect.Heart)]));
                break;
            }
            case Social.Tossed:
                // Putin's judo: tumble in place, then get up cross.
                Interrupt([Play(Anim.Surprised, 0.5, Effect.Exclaim),
                           Face(DirectionTo(other)),
                           Say(Line.Hit, other.Character),
                           Run(() => { window.Impact(0); window.Shake(); }),
                           Play(Anim.Dragged, 0.6),
                           Play(Character.GrumpyAnim, 1.6, Effect.Anger),
                           AskForComfort()]);
                break;
            case Social.Applaud:
                Interrupt([Face(DirectionTo(other)),
                           Say(Line.Cheer, other.Character),
                           Play(Anim.Happy, 1.8, Effect.Note)]);
                break;
            case Social.Stunned:
                Interrupt([Face(DirectionTo(other)),
                           Play(Anim.Surprised, 3.6, Effect.Dots),
                           Play(Anim.Sad, 1, Effect.Sweat)]);
                break;
        }
    }

    // MARK: - Skills

    /// A special move picked from the right-click menu.
    public void UseSkill(Skill skill)
    {
        if (Agent != null || dragging || leaving) return;
        Interrupt(SkillPlan(skill));
    }

    private List<Step> SkillPlan(Skill skill)
    {
        switch (skill)
        {
            case Skill.MindRead:
            {
                // Whatever app you are in is what's on your mind.
                var app = FrontmostAppName();
                return Engage([Run(() => window.Telepathy()),
                               Play(Anim.Skill1, 2),
                               Run(() => SayLine(app == null ? Line.MindReadIdle : Line.MindRead, app: app ?? "")),
                               Play(Anim.Skill2, 2.4)]);
            }
            case Skill.ReadFriend:
            {
                if (NearbyFriends(900).RandomElement() is not { } friend) return SkillPlan(Skill.MindRead);
                friend.Receive(Social.Invite, this);
                return Engage([Walk(Beside(friend)),
                               Face(DirectionTo(friend)),
                               Run(() => window.Telepathy()),
                               Play(Anim.Skill1, 1.5),
                               Run(() => friend.Receive(Social.MindRead, this)),
                               Play(Anim.Skill1, 1.6),
                               Say(Line.ReadFriend, friend.Character),
                               Play(Anim.Skill2, 2.2)]);
            }
            case Skill.BloodBurst:
                return Engage([Play(Anim.Skill2, 1, say: Line.BloodBurst),
                               Run(() =>
                               {
                                   window.FlameBurst();
                                   Blast(new PointD(Position.X, Position.Y + 50), 210, _ => true);
                               }),
                               Play(Anim.Skill1, 1.5),
                               Play(Anim.Happy, 1, Effect.Sparkle)]);
            case Skill.Resize:
            {
                var small = Mathx.Coin();
                return [Run(() =>
                        {
                            window.Puff();
                            targetScale = small ? 0.55 : 1.25;
                            resizedUntil = Now + Mathx.Random(12, 20);
                        }),
                        Play(Anim.Surprised, 0.5),
                        Say(small ? Line.Shrink : Line.Grow),
                        Play(Anim.Happy, 1.4, Effect.Note)];
            }
            case Skill.FlowerField:
                return Engage([Play(Anim.Skill1, 1, say: Line.Flowers),
                               Run(() => window.FlowerField(7.5)),
                               Play(Anim.Skill1, 2),
                               Play(Anim.Idle, 4.5)]);
            case Skill.Mimic:
            {
                // The chest turns up beside her, on whichever side has room; she ends up inside it.
                PointD ChestSpot(double s) => Confined(ClampToScreens(new PointD(Position.X + s * 48, Position.Y)));
                var side = Math.Abs(ChestSpot(Facing).X - Position.X) < 40 ? -Facing : Facing;
                var spot = ChestSpot(side);
                return Engage([Face(side),
                               Run(() => window.ShowProp(SpriteSheet.Effect(Effect.Chest, SpriteSheet.FramePixels), spot.X - Position.X)),
                               Play(Anim.Surprised, 0.6, Effect.Sparkle, Line.MimicSpot),
                               Play(Anim.Idle, 2.2),
                               Run(() =>
                               {
                                   window.ShowProp(null, 0);
                                   Position = spot;
                               }),
                               Play(Anim.Skill2, 3.5, say: Line.MimicEaten),
                               Run(() => window.Puff()),
                               Play(Anim.Sad, 1.8, Effect.Sweat, Line.MimicOut)]);
            }
            case Skill.GuitarSolo:
                foreach (var friend in NearbyFriends(600)) friend.Receive(Social.Listen, this);
                return Engage([Play(Anim.Surprised, 0.5, Effect.Sweat, Line.Guitar),
                               Run(() => window.Notes(6, Facing)),
                               Play(Anim.Skill1, 6.5),
                               Say(Line.GuitarDone),
                               Play(Anim.Happy, 1.8, Effect.Heart)]);
            case Skill.Melt:
                return Engage([Play(Anim.Surprised, 0.5, Effect.Sweat, Line.Melt),
                               Hold(Anim.Skill2, 3.5),
                               Run(() => window.Puff()),
                               Play(Anim.Surprised, 0.4),
                               Play(Anim.Sad, 1.6, say: Line.Reform)]);
            case Skill.MorningStar:
                // The ball hits the ground on frame 5 of the 8-frame swing.
                return Engage([Say(Line.MorningStar),
                               Hold(Anim.Skill1, 0.625),
                               Run(() =>
                               {
                                   var side = Facing;
                                   window.Impact(44 * side);
                                   var origin = new PointD(Position.X, Position.Y + 30);
                                   Blast(origin, 230, p => (p.X - origin.X) * side > 0 && Math.Abs(p.Y - origin.Y) < 140);
                               }),
                               Play(Anim.Skill1, 0.375),
                               Play(Anim.Happy, 1, Effect.Sparkle)]);
            case Skill.OniForm:
                return Engage([Play(Anim.Surprised, 0.4, Effect.Exclaim),
                               Run(() => window.Aura(4)),
                               Play(Anim.Skill2, 4, say: Line.Oni),
                               Say(Line.OniCalm),
                               Play(Anim.Happy, 1.4)]);
            case Skill.Wall:
                // The wall rises in front of him and shoves the icons beyond it away.
                return Engage([Run(() =>
                               {
                                   window.Wall(3.6, Facing);
                                   var origin = new PointD(Position.X + Facing * 70, Position.Y + 40);
                                   Blast(origin, 260, p => (p.X - origin.X) * Facing > -20);
                               }),
                               Play(Anim.Skill1, 1, say: Line.Wall),
                               Hold(Anim.Skill1, 1.2),
                               Play(Anim.Happy, 1.4, Effect.Sparkle, Line.WallBuilt)]);
            case Skill.Tariff:
            {
                // Holds up the tariff board; coins pour off it at a friend, or at the icons ahead.
                var friend = NearbyFriends(700).RandomElement();
                friend?.Receive(Social.Invite, this);
                List<Step> approach = friend != null ? [Walk(Beside(friend)), Face(DirectionTo(friend))] : [];
                return Engage([.. approach,
                               Say(Line.Tariff, friend?.Character),
                               Play(Anim.Skill2, 1),
                               Run(() =>
                               {
                                   window.Coins(Facing);
                                   if (friend != null)
                                   {
                                       Dispatch.After(0.4, () => friend.Receive(Social.Hit, this));
                                   }
                                   else
                                   {
                                       var origin = new PointD(Position.X + Facing * 50, Position.Y + 60);
                                       Blast(origin, 260, p => (p.X - origin.X) * Facing > 0);
                                   }
                               }),
                               Hold(Anim.Skill2, 1.3),
                               Play(Anim.Happy, 1.4, Effect.Note)]);
            }
            case Skill.Judo:
            {
                if (NearbyFriends(500).RandomElement() is not { } friend)
                    return Engage([Play(Anim.Skill1, 2, say: Line.Judo), Play(Anim.Happy, 1, Effect.Sparkle)]);
                friend.Receive(Social.Invite, this);
                return Engage([Walk(Beside(friend)),
                               Face(DirectionTo(friend)),
                               Play(Anim.Skill1, 1.8, say: Line.Judo),
                               Run(() => friend.Receive(Social.Tossed, this)),
                               Play(Anim.Happy, 1.2)]);
            }
            case Skill.BearRide:
                return Engage([Run(() =>
                               {
                                   window.Dust(3, Facing);
                                   window.Shake();
                               }),
                               Play(Anim.Skill2, 1, say: Line.Bear),
                               Run(() => Blast(new PointD(Position.X, Position.Y + 30), 190, _ => true)),
                               Play(Anim.Skill2, 2),
                               Play(Anim.Happy, 1.2, Effect.Sparkle)]);
            case Skill.Parade:
                // A convoy rolls past while he waves, and the nearby icons fall in line.
                return Engage([Run(() => window.Convoy(4.2, Facing)),
                               Play(Anim.Skill1, 1, say: Line.Parade),
                               Run(() =>
                               {
                                   // Up to five nearby icons line up in a neat row in front of him.
                                   var icons = world.AvailableIcons().Where(i => Mathx.Distance(i.Center, Position) < 320).Take(5).ToList();
                                   if (icons.Count == 0) return;
                                   var frame = ScreenFrame(Position);
                                   var y = Mathx.Clamp(Position.Y + 60, frame.MinY + 60, frame.MaxY - 60);
                                   var paths = new Dictionary<string, List<PointD>>();
                                   for (var i = 0; i < icons.Count; i++)
                                   {
                                       var x = Position.X + Facing * (i + 1) * 64;
                                       paths[icons[i].Name] = [new PointD(Mathx.Clamp(x, frame.MinX + 40, frame.MaxX - 40), y)];
                                   }
                                   world.Desktop.MoveAll(paths);
                               }),
                               Play(Anim.Skill1, 2.6, say: Line.ParadeOrder),
                               Play(Anim.Happy, 1.2, Effect.Sparkle)]);
            case Skill.AiRobot:
                // The robot is printed and its eyes light up, then it sweeps the icons ahead with a laser.
                return Engage([Play(Anim.Skill2, 1, say: Line.AiRobot),
                               Run(() =>
                               {
                                   window.Laser(1.6, Facing);
                                   var eye = new PointD(Position.X + Facing * 43, Position.Y + 62);
                                   Blast(eye, 420, p => (p.X - eye.X) * Facing > 0 && Math.Abs(p.Y - eye.Y) < 90);
                               }),
                               Hold(Anim.Skill2, 1.6),
                               Say(Line.AiRobotDone),
                               Play(Anim.Happy, 1.2, Effect.Sparkle)]);
            case Skill.Applause:
                foreach (var friend in NearbyFriends(700)) friend.Receive(Social.Applaud, this);
                return Engage([Run(() => window.Confetti(2.4)),
                               Play(Anim.Skill1, 2.4, say: Line.Applause),
                               Play(Anim.Happy, 1.4, Effect.Note)]);
            case Skill.Missile:
                // Launch (one cycle of skill2), then the missile comes down as fireworks.
                return Engage([Play(Anim.Skill2, 1, say: Line.Missile),
                               Run(() =>
                               {
                                   window.Fireworks(Facing);
                                   Blast(new PointD(Position.X + Facing * 44, Position.Y + 128), 260, _ => true);
                               }),
                               Say(Line.Boom),
                               Hold(Anim.Skill2, 1.8),
                               Play(Anim.Happy, 1.2, Effect.Sparkle)]);
            case Skill.WaterBreathing:
                return Engage([Play(Anim.Skill1, 0.5, say: Line.WaterBreathing),
                               Run(() =>
                               {
                                   window.Slash(WaterColor, Facing, 2);
                                   BlastAhead(240);
                               }),
                               Play(Anim.Skill1, 1.5),
                               Play(Anim.Happy, 1, Effect.Sparkle)]);
            case Skill.Hinokami:
                return Engage([Play(Anim.Skill2, 0.5, say: Line.Hinokami),
                               Run(() =>
                               {
                                   window.Slash(FireColor, Facing, 3, full: true);
                                   window.Sparks(FireColor, 2);
                                   BlastAround(220);
                               }),
                               Play(Anim.Skill2, 2),
                               Play(Anim.Happy, 1, Effect.Sparkle)]);
            case Skill.Thunderclap:
                // Dozes off, then strikes in his sleep and wakes up wondering what happened.
                return Engage([Play(Anim.Sleep, 1.4, Effect.Zzz),
                               Say(Line.Thunderclap),
                               Run(() => window.Sparks(ThunderColor, 0.8)),
                               Play(Anim.Skill1, 0.8),
                               Run(() =>
                               {
                                   window.Lightning(0, ThunderColor);
                                   Dash(260, ThunderColor);
                               }),
                               Play(Anim.Skill1, 1.2),
                               Play(Anim.Surprised, 0.8, Effect.Exclaim)]);
            case Skill.ClingCry:
            {
                // Clings to a friend in tears until they come and comfort him.
                var friend = NearbyFriends(700).RandomElement();
                friend?.Receive(Social.Invite, this);
                List<Step> approach = friend != null ? [Walk(Beside(friend)), Face(DirectionTo(friend))] : [];
                return Engage([.. approach,
                               Say(Line.ClingCry),
                               Play(Anim.Skill2, 3, Effect.Sweat),
                               Run(() => friend?.Comfort(this))]);
            }
            case Skill.GomuPistol:
                // The fist lands on frame 4 of the 8-frame stretch.
                return Engage([Play(Anim.Skill1, 0.5, say: Line.GomuPistol),
                               Run(() =>
                               {
                                   window.Impact(Facing * 112);
                                   BlastAhead(150, reach: 100);
                               }),
                               Play(Anim.Skill1, 0.5),
                               Play(Anim.Happy, 1.2, Effect.Note)]);
            case Skill.Gear5:
                foreach (var friend in NearbyFriends(600)) friend.Receive(Social.Applaud, this);
                return Engage([Run(() =>
                               {
                                   window.Aura(3.5, Rgba.Gray(1));
                                   window.Sparks(Rgba.Gray(1), 3);
                               }),
                               Play(Anim.Skill2, 3.5, say: Line.Gear5),
                               Play(Anim.Happy, 1.2, Effect.Note)]);
            case Skill.ThreeSword:
                return Engage([Play(Anim.Skill1, 0.5, say: Line.ThreeSword),
                               Run(() =>
                               {
                                   window.Slash(SteelColor, Facing, 3);
                                   BlastAhead(250);
                               }),
                               Play(Anim.Skill1, 1.5),
                               Play(Anim.Idle, 0.6)]);
            case Skill.GetLost:
                // Sets off confidently somewhere else entirely.
                return Engage([Say(Line.GetLost),
                               Play(Anim.Skill2, 2),
                               Walk(RandomDestination()),
                               Play(Anim.Skill2, 1.5, Effect.Dots),
                               Say(Line.LostAgain),
                               Play(Anim.Grumpy, 1.2, Effect.Sweat)]);
            case Skill.ClimaTact:
                return Engage([Play(Anim.Skill1, 1, say: Line.ClimaTact),
                               Run(() =>
                               {
                                   window.Lightning(Facing * 80);
                                   Blast(new PointD(Position.X + Facing * 80, Position.Y + 30), 170, _ => true);
                               }),
                               Play(Anim.Skill1, 1),
                               Play(Anim.Happy, 1, Effect.Sparkle)]);
            case Skill.Berries:
                return Engage([Play(Anim.Skill2, 0.5, say: Line.Berries),
                               Run(() => window.Coins(Facing)),
                               Play(Anim.Skill2, 2.5),
                               Play(Anim.Happy, 1.2, Effect.Heart)]);
            case Skill.TitanShift:
                // A bolt from the sky, and he comes out of the smoke a lot bigger.
                return Engage([Play(Anim.Surprised, 0.4, Effect.Exclaim),
                               Run(() =>
                               {
                                   window.Lightning(0);
                                   window.Puff();
                                   window.Shake();
                                   targetScale = 1.35;
                                   resizedUntil = Now + 5;
                                   BlastAround(250);
                               }),
                               Play(Anim.Skill1, 4.4, say: Line.TitanShift),
                               Play(Anim.Idle, 0.8)]);
            case Skill.Tatakae:
                foreach (var friend in NearbyFriends(600)) friend.Receive(Social.Applaud, this);
                return Engage([Run(() => window.Aura(2.5, ScarletColor)),
                               Play(Anim.Skill2, 2.5, say: Line.Tatakae),
                               Play(Anim.Happy, 1, Effect.Exclaim)]);
            case Skill.OdmGear:
                return Engage([Play(Anim.Skill1, 0.4, say: Line.OdmGear),
                               Run(() =>
                               {
                                   Dash(260, SteelColor);
                                   window.Slash(SteelColor, Facing);
                               }),
                               Play(Anim.Skill1, 1.2),
                               Play(Anim.Idle, 0.6)]);
            case Skill.Scarf:
                return [Play(Anim.Skill2, 3, Effect.Heart, Line.Scarf)];
            case Skill.SpinSlash:
                return Engage([Play(Anim.Skill1, 0.3, say: Line.SpinSlash),
                               Run(() =>
                               {
                                   window.Slash(SteelColor, Facing, 3, full: true);
                                   BlastAround(210);
                               }),
                               Play(Anim.Skill1, 1.7),
                               Play(Anim.Idle, 0.8)]);
            case Skill.Cleaning:
            {
                // Ties on his bandana and sweeps, then tidies the desktop for real.
                var tidy = !InRoom && world.Arranger == null ? ArrangePlan() : [];
                return [Play(Anim.Skill2, 2.5, say: Line.Cleaning), .. tidy];
            }
            case Skill.DivergentFist:
                // The punch, then the delayed second impact of cursed energy.
                return Engage([Play(Anim.Skill1, 0.5, say: Line.DivergentFist),
                               Run(() =>
                               {
                                   window.Shockwave(CursedColor, Facing * 34, 62, 2);
                                   BlastAhead(230);
                               }),
                               Play(Anim.Skill1, 1),
                               Play(Anim.Happy, 1, Effect.Sparkle)]);
            case Skill.Sukuna:
                return Engage([Play(Anim.Surprised, 0.4, Effect.Exclaim),
                               Run(() => window.Aura(3.5, ScarletColor)),
                               Play(Anim.Skill2, 3.5, say: Line.Sukuna),
                               Run(() => window.Puff()),
                               Play(Anim.Surprised, 0.8, Effect.Sweat)]);
            case Skill.InfiniteVoid:
                foreach (var friend in NearbyFriends(600)) friend.Receive(Social.Stunned, this);
                return Engage([Play(Anim.Skill1, 0.6, say: Line.InfiniteVoid),
                               Run(() => window.Domain(4)),
                               Play(Anim.Skill1, 3.6),
                               Play(Anim.Happy, 1.2, Effect.Sparkle)]);
            case Skill.HollowPurple:
                // Blue and red come together into purple, then it fires.
                return Engage([Play(Anim.Skill2, 1, say: Line.HollowPurple),
                               Run(() =>
                               {
                                   window.Beam(PurpleColor, Facing, 34, 62, 1.4);
                                   var eye = new PointD(Position.X + Facing * 34, Position.Y + 62);
                                   Blast(eye, 440, p => (p.X - eye.X) * Facing > 0 && Math.Abs(p.Y - eye.Y) < 110);
                               }),
                               Hold(Anim.Skill2, 1.4),
                               Play(Anim.Happy, 1.2, Effect.Sparkle)]);
            case Skill.DivineDogs:
                return Engage([Play(Anim.Skill1, 0.8, say: Line.DivineDogs),
                               Run(() =>
                               {
                                   window.Runners(Effect.Dog, 2, Facing);
                                   Dispatch.After(0.4, () => BlastAhead(280));
                               }),
                               Play(Anim.Skill1, 1.2),
                               Play(Anim.Idle, 0.6)]);
            case Skill.RabbitEscape:
                return Engage([Play(Anim.Skill2, 0.6, say: Line.RabbitEscape),
                               Run(() => window.Runners(Effect.Rabbit, 12, Facing, 30, scatter: true)),
                               Play(Anim.Skill2, 1.8),
                               Play(Anim.Idle, 0.6)]);
            case Skill.Jajanken:
                // Charges through rock, paper... and the last frame is the punch.
                return Engage([Say(Line.Jajanken),
                               Hold(Anim.Skill1, 1),
                               Run(() =>
                               {
                                   window.Shockwave(ThunderColor, Facing * 36, 56, 3);
                                   window.Shake();
                                   BlastAhead(250);
                               }),
                               Hold(Anim.Skill1, 0.6),
                               Play(Anim.Happy, 1.2, Effect.Sparkle)]);
            case Skill.Fishing:
                return Engage([Say(Line.Fishing),
                               Play(Anim.Skill2, 3.5),
                               Run(() => window.Shockwave(WaterColor, Facing * 70, 6, 2, to: 2)),
                               Play(Anim.Happy, 1.8, Effect.Sparkle, Line.FishCaught)]);
            case Skill.Godspeed:
            {
                // Zips back and forth in a few flashes of lightning.
                Step Zap() => Run(() =>
                {
                    Facing = Mathx.Coin() ? 1 : -1;
                    Dash(150, ThunderColor);
                    window.Lightning(0, CursedColor);
                });
                return Engage([Play(Anim.Skill1, 0.6, say: Line.Godspeed),
                               Run(() => window.Sparks(CursedColor, 2)),
                               Zap(), Play(Anim.Skill1, 0.4), Zap(), Play(Anim.Skill1, 0.4), Zap(),
                               Play(Anim.Skill1, 0.6),
                               Play(Anim.Happy, 1, Effect.Sparkle)]);
            }
            case Skill.Chocolate:
                return [Play(Anim.Skill2, 2.5, Effect.Heart, Line.Chocolate)];
            case Skill.ChainJail:
                // The chain shoots out and drags the icons ahead back in a neat row at his feet.
                return Engage([Play(Anim.Skill1, 0.5, say: Line.ChainJail),
                               Run(() =>
                               {
                                   window.Chain(Facing);
                                   if (InRoom) return;
                                   var icons = world.AvailableIcons()
                                       .Where(i => (i.Center.X - Position.X) * Facing > 0 && Mathx.Distance(i.Center, Position) < 380)
                                       .Take(4).ToList();
                                   var frame = ScreenFrame(Position);
                                   var paths = new Dictionary<string, List<PointD>>();
                                   for (var i = 0; i < icons.Count; i++)
                                   {
                                       var x = Position.X + Facing * (i + 1) * 60;
                                       paths[icons[i].Name] = [new PointD(Mathx.Clamp(x, frame.MinX + 40, frame.MaxX - 40),
                                           Mathx.Clamp(Position.Y + 30, frame.MinY + 40, frame.MaxY - 60))];
                                   }
                                   if (paths.Count > 0) world.Desktop.MoveAll(paths);
                               }),
                               Play(Anim.Skill1, 1.5),
                               Play(Anim.Idle, 0.6)]);
            case Skill.ScarletEyes:
                return Engage([Play(Anim.Surprised, 0.4, Effect.Exclaim),
                               Run(() => window.Aura(3, ScarletColor)),
                               Play(Anim.Skill2, 3, say: Line.ScarletEyes),
                               Play(Anim.Idle, 0.8)]);
            case Skill.KnightOath:
                // Raises the sword (frames 0–3), then cuts on the way down and holds the finish.
                return Engage([Say(Line.KnightOath),
                               Run(() => window.Sparks(ThunderColor, 0.5)),
                               Hold(Anim.Skill1, 0.5),
                               Run(() =>
                               {
                                   window.Slash(SteelColor, Facing);
                                   BlastAhead(220);
                               }),
                               Hold(Anim.Skill1, 1),
                               Play(Anim.Happy, 1.2, Effect.Heart)]);
            case Skill.ShieldBash:
                // The shield hits on frame 3 of the 8-frame shove.
                return Engage([Play(Anim.Skill2, 0.4, say: Line.ShieldBash),
                               Run(() =>
                               {
                                   window.Impact(Facing * 52);
                                   window.Shake();
                                   BlastAhead(200, reach: 30);
                               }),
                               Play(Anim.Skill2, 1.6),
                               Play(Anim.Happy, 1.2, Effect.Sparkle)]);
            case Skill.GuguChant:
                // Frame 4 (zero-based) opens the flippers for the big "gaga!".
                return Engage([Say(Line.GuguChant),
                               Hold(Anim.Skill1, 0.5),
                               Run(() =>
                               {
                                   window.Sparks(ThunderColor, 0.7);
                                   foreach (var friend in NearbyFriends(420)) friend.Receive(Social.Applaud, this);
                               }),
                               Hold(Anim.Skill1, 0.5),
                               Play(Anim.Happy, 1.2, Effect.Heart)]);
            default: // BellySlide
                // Frame 3 starts the belly slide; hold continues the same animation clock.
                return Engage([Say(Line.BellySlide),
                               Hold(Anim.Skill2, 0.375),
                               Run(() => Dash(140, WaterColor)),
                               Hold(Anim.Skill2, 0.625),
                               Play(Anim.Happy, 1, Effect.Sparkle)]);
        }
    }

    /// Colors for the anime series' moves.
    private static readonly Rgba WaterColor = new(0.3, 0.62, 1, 0.9);
    private static readonly Rgba FireColor = new(1, 0.5, 0.15, 0.9);
    private static readonly Rgba SteelColor = new(0.85, 0.9, 0.95, 0.9);
    private static readonly Rgba ThunderColor = new(1, 0.85, 0.25);
    private static readonly Rgba CursedColor = new(0.35, 0.55, 1);
    private static readonly Rgba PurpleColor = new(0.65, 0.3, 1);
    private static readonly Rgba ScarletColor = new(1, 0.15, 0.2);

    /// The app the user is in (for Anya's mind reading), or null when it's this one.
    private static string? FrontmostAppName()
    {
        try
        {
            Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out var pid);
            if (pid == 0 || pid == Environment.ProcessId) return null;
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            string? description = null;
            try { description = process.MainModule?.FileVersionInfo.FileDescription; } catch { }
            return string.IsNullOrWhiteSpace(description) ? process.ProcessName : description;
        }
        catch { return null; }
    }

    /// Blasts the icons (and friends) in front of the pet, from about waist height.
    private void BlastAhead(double radius, double reach = 0)
    {
        var origin = new PointD(Position.X + Facing * reach, Position.Y + 50);
        var facing = Facing;
        Blast(origin, radius, p => (p.X - origin.X) * facing > -20);
    }

    /// Blasts everything around the pet.
    private void BlastAround(double radius) => Blast(new PointD(Position.X, Position.Y + 50), radius, _ => true);

    /// Jumps `distance` points ahead in an instant (staying on screen, or on the room's floor), clearing the way.
    private void Dash(double distance, Rgba color)
    {
        var from = Position;
        Position = Confined(ClampToScreens(new PointD(Position.X + Facing * distance, Position.Y)));
        window.Streak(Facing, color);
        Blast(new PointD((from.X + Position.X) / 2, Position.Y + 50), Math.Abs(Position.X - from.X) / 2 + 60, _ => true);
    }

    /// Friends on the same side of the room walls who are free to join in.
    private List<Pet> NearbyFriends(double distance) =>
        world.Pets.Values.Where(p => p != this && p.IsAvailable && p.InRoom == InRoom && Mathx.Distance(p.Position, Position) < distance).ToList();

    /// Sends the desktop icons within `radius` of `origin` that `hits` accepts flying outward (never from inside
    /// the room), and catches friends standing there.
    private void Blast(PointD origin, double radius, Func<PointD, bool> hits)
    {
        if (closed) return;
        bool Reached(PointD p) => Mathx.Distance(p, origin) < radius && hits(p);
        foreach (var other in world.Pets.Values.Where(o => o != this && o.Agent == null && o.InRoom == InRoom
                     && Reached(new PointD(o.Position.X, o.Position.Y + 55))).ToList())
            Dispatch.After(0.2, () => other.Receive(Social.Hit, this));
        if (InRoom) return;
        var paths = new Dictionary<string, List<PointD>>();
        foreach (var icon in world.AvailableIcons().Where(i => Reached(i.Center)))
        {
            var dx = icon.Center.X - origin.X;
            var dy = icon.Center.Y - origin.Y;
            var length = Math.Max(Mathx.Hypot(dx, dy), 1);
            paths[icon.Name] = FlightPath(icon.Center, dx / length, dy / length, Mathx.Random(150, 260));
            world.Claim(icon.Name);
        }
        if (paths.Count == 0) return;
        world.Desktop.MoveAll(paths, completion: _ => { foreach (var name in paths.Keys) world.Release(name); });
    }

    // MARK: - Room

    /// Shut in the play room: stays on its floor and only plays with roommates.
    public bool InRoom => Agent == null && world.Room.IsOpen && world.Room.Members.Contains(Character.Id);

    /// Where the pet may go: anywhere, or only onto the room's floor while shut in.
    private PointD Confined(PointD p)
    {
        if (!InRoom) return p;
        var f = world.Room.Floor;
        return new PointD(Mathx.Clamp(p.X, f.MinX, f.MaxX), Mathx.Clamp(p.Y, f.MinY, f.MaxY));
    }

    /// Pops onto the room's floor when put in the room, dropping whatever it was busy with.
    public void MoveIntoRoom()
    {
        if (!InRoom || world.Room.Floor.Contains(Position)) return;
        if (Carrying is { } name) Drop(name, CarryPoint);
        if (kickTarget is { } target) { world.Release(target); kickTarget = null; }
        if (world.Arranger == this) world.Arranger = null;
        window.ShowProp(null, 0);
        Position = world.Room.RandomSpot();
        steps.Clear();
        steps.AddRange([Play(Anim.Surprised, 0.5, Effect.Sparkle), Play(Anim.Happy, 1.4, Effect.Heart, Line.Happy)]);
        current = null;
        holding = false;
        engaged = false;
    }

    /// Moves along with the room when it is moved or resized.
    public void FollowRoom(double dx, double dy)
    {
        if (!InRoom) return;
        Position = Confined(new PointD(Position.X + dx, Position.Y + dy));
    }

    // MARK: - Mouse

    public void Clicked()
    {
        if (leaving) return;
        // Clicking a mascot that is waiting for you jumps to its terminal; it then hides until the agent acts again.
        if (Agent is { } agent && agent.State is AgentState.Done or AgentState.NeedsInput)
        {
            AgentMonitor.FocusHostApp(agent.Pid);
            Interrupt([Play(Anim.Happy, 0.6, Effect.Heart), Run(Disappear)]);
            return;
        }
        var t = Now;
        clicks = clicks.Where(c => t - c < 3).Append(t).ToList();
        if (anim == Anim.Sleep)
        {
            Interrupt([Play(Anim.Surprised, 0.7, Effect.Exclaim),
                       Play(Character.GrumpyAnim, 1.8, Effect.Anger, Line.Wake)]);
        }
        else if (clicks.Count >= 4)
        {
            clicks.Clear();
            // Too much attention: Bocchi melts, everyone else gets cross.
            Interrupt(Character.Skills.Contains(Skill.Melt) ? SkillPlan(Skill.Melt) : [Play(Character.GrumpyAnim, 2.2, Effect.Anger, Line.Poke)]);
        }
        else
        {
            boredom = 0;
            Interrupt([Play(Anim.Happy, 1.3, Effect.Heart, Line.Click)]);
        }
    }

    public void BeginDrag()
    {
        dragging = true;
        holding = false;
        effect = (Effect.Sweat, Now, double.PositiveInfinity);
    }

    public void Drag(PointD p) => Position = InRoom ? world.Room.ClampHeld(p) : p;

    public void EndDrag()
    {
        dragging = false;
        effect = null;
        var floorTop = world.Room.Floor.MaxY;
        List<Step> landing = InRoom && Position.Y > floorTop ? [new FallStep(floorTop)] : [];
        Interrupt([.. landing,
                   Play(Anim.Surprised, 0.5),
                   Mathx.Coin() ? Play(Anim.Happy, 1.2, Effect.Note, Line.Happy)
                                : Play(Character.GrumpyAnim, 1.5, Effect.Anger, Line.Grumpy)]);
    }

    private void UpdateMousePassthrough()
    {
        var m = Screens.Mouse;
        var local = new PointD(m.X - window.FullFrame.MinX, m.Y - window.FullFrame.MinY);
        var over = dragging || PetWindow.BodyRect.Contains(local);
        window.ClickThrough = !over;
    }

    // MARK: - Screen geometry

    private static RectD ScreenFrame(PointD p)
    {
        var screens = Screens.All;
        foreach (var s in screens) if (s.Frame.Contains(p)) return s.Visible;
        return screens.MinBy(s => Distance(p, s.Frame)).Visible;
    }

    private static double Distance(PointD p, RectD r) =>
        Mathx.Hypot(Math.Max(Math.Max(r.MinX - p.X, 0), p.X - r.MaxX), Math.Max(Math.Max(r.MinY - p.Y, 0), p.Y - r.MaxY));

    /// Keeps the whole character inside a screen's visible area.
    private static PointD ClampToScreens(PointD p)
    {
        var f = ScreenFrame(p);
        return new PointD(Mathx.Clamp(p.X, f.MinX + 40, f.MaxX - 40), Mathx.Clamp(p.Y, f.MinY, f.MaxY - 130));
    }

    private PointD RandomDestination()
    {
        if (InRoom) return world.Room.RandomSpot();
        var screens = Screens.All;
        var here = screens.FirstOrDefault(s => s.Frame.Contains(Position), screens[0]);
        var screen = Random.Shared.NextDouble() < 0.7 ? here : screens.RandomElement();
        var f = screen.Visible;
        return new PointD(Mathx.Random(f.MinX + 60, f.MaxX - 60),
            Mathx.Random(f.MinY, f.MinY + Math.Max(0, (f.Height - 140) * 0.6)));
    }
}

/// Orders names the way Finder and Explorer do ("file2" before "file10").
sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string a, string b);

    public int Compare(string? x, string? y) => StrCmpLogicalW(x ?? "", y ?? "");
}
