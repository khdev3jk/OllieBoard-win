namespace OllieBoard;

enum Anim
{
    Idle, Walk, Run, Kick, Carry, Happy, Bored, Yawn, Sleep, Grumpy, Dragged, Surprised, Work, Sad,
    /// Each character's two special moves (see Skill); empty for agent mascots.
    Skill1, Skill2,
}

enum Effect
{
    Heart, Note, Anger, Zzz, Dots, Sweat, Exclaim, Sparkle, Code, Check,
    // Props and particles for the special moves.
    Chest, Flower1, Flower2, Flower3, Flame, Glow, Puff, Wall, Coin, Boom, Tank, Truck, Dog, Rabbit,
}

enum Behavior { Wander, Kick, Arrange, Bored, Sleep, Grumpy, Happy, Social, Skill }

/// Special moves from each character's story, used now and then or picked from the right-click menu.
enum Skill
{
    MindRead, ReadFriend,       // Anya
    BloodBurst, Resize,         // Nezuko
    FlowerField, Mimic,         // Frieren
    GuitarSolo, Melt,           // Bocchi
    MorningStar, OniForm,       // Rem
    Wall, Tariff,               // Trump
    Judo, BearRide,             // Putin
    Parade, AiRobot,            // Xi
    Applause, Missile,          // Kim
    WaterBreathing, Hinokami,   // Tanjiro
    Thunderclap, ClingCry,      // Zenitsu
    GomuPistol, Gear5,          // Luffy
    ThreeSword, GetLost,        // Zoro
    ClimaTact, Berries,         // Nami
    TitanShift, Tatakae,        // Eren
    OdmGear, Scarf,             // Mikasa
    SpinSlash, Cleaning,        // Levi
    DivergentFist, Sukuna,      // Yuji
    InfiniteVoid, HollowPurple, // Gojo
    DivineDogs, RabbitEscape,   // Megumi
    Jajanken, Fishing,          // Gon
    Godspeed, Chocolate,        // Killua
    ChainJail, ScarletEyes,     // Kurapika
    KnightOath, ShieldBash,     // Cat Knight
    GuguChant, BellySlide,      // Gugugaga
}

/// Groups the characters in the menu. The five supported series and politics also have a play-room scene.
enum Series
{
    Kimetsu, OnePiece, AttackOnTitan, JujutsuKaisen, HunterXHunter, SpyFamily, Frieren, Bocchi, ReZero,
    CatKnight, Endfield, Politics,
}

static class Names
{
    /// The key used in layout.json and dialogue.json: the case name with a lowercase first letter.
    public static string Key<T>(this T value) where T : struct, Enum
    {
        var name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    public static bool IsSkill(this Anim anim) => anim is Anim.Skill1 or Anim.Skill2;

    public static string Title(this Skill skill) => Dialogue.SkillName(skill);

    /// Series names stay as they are; only "politics" is translated.
    public static string Title(this Series series) => series switch
    {
        Series.Kimetsu => "Kimetsu no Yaiba",
        Series.OnePiece => "One Piece",
        Series.AttackOnTitan => "Attack on Titan",
        Series.JujutsuKaisen => "Jujutsu Kaisen",
        Series.HunterXHunter => "Hunter x Hunter",
        Series.SpyFamily => "Spy x Family",
        Series.Frieren => "Sousou no Frieren",
        Series.Bocchi => "Bocchi the Rock!",
        Series.ReZero => "Re:Zero",
        Series.CatKnight => "Cat Knight",
        Series.Endfield => "Arknights: Endfield",
        _ => UI.Text(UIText.Politics),
    };
}

/// Personality only; what each character says lives in Assets/dialogue.json.
sealed record Character(
    string Id,
    string Name,
    /// Null for agent mascots.
    Series? Series,
    double WalkSpeed,
    Dictionary<Behavior, double> Weights,
    /// Animation used when the character is upset (Bocchi sulks instead of stomping).
    Anim GrumpyAnim,
    /// Runs away from the mouse cursor.
    bool Shy,
    /// Likes waking up sleeping friends.
    bool Prankster,
    Skill[] Skills,
    /// Agent mascots: a shared dialogue table and a fixed display name.
    string? DialogueId = null,
    string? FixedName = null)
{
    public static Dictionary<Behavior, double> W(double wander, double kick, double arrange, double bored, double sleep,
        double grumpy, double happy, double social, double skill = 0) => new()
    {
        [Behavior.Wander] = wander, [Behavior.Kick] = kick, [Behavior.Arrange] = arrange, [Behavior.Bored] = bored,
        [Behavior.Sleep] = sleep, [Behavior.Grumpy] = grumpy, [Behavior.Happy] = happy, [Behavior.Social] = social,
        [Behavior.Skill] = skill,
    };

    /// The mascot for one running session of an agent (its sprite sheet is named after the agent).
    public static Character ForAgent(AgentKind kind, int pid) => new(
        $"agent-{pid}", kind.Title(), null, 95, W(3, 1, 0.5, 1, 0.5, 0.3, 1.5, 1.5),
        Anim.Grumpy, false, false, [], DialogueId: "agent", FixedName: kind.Title());

    public static readonly Character[] All =
    [
        new("anya", "Anya Forger", OllieBoard.Series.SpyFamily, 95, W(3, 3, 1, 1.5, 1, 1, 2, 2.5, 1.2),
            Anim.Grumpy, false, true, [Skill.MindRead, Skill.ReadFriend]),
        new("nezuko", "Nezuko Kamado", OllieBoard.Series.Kimetsu, 100, W(3, 2, 1, 1, 2.5, 0.5, 2, 2, 1),
            Anim.Grumpy, false, false, [Skill.BloodBurst, Skill.Resize]),
        new("frieren", "Frieren", OllieBoard.Series.Frieren, 70, W(2, 1, 0.7, 3, 3, 0.5, 0.5, 0.8, 1.2),
            Anim.Grumpy, false, false, [Skill.FlowerField, Skill.Mimic]),
        new("bocchi", "Hitori Gotoh", OllieBoard.Series.Bocchi, 85, W(2, 0.7, 1, 2, 1, 1, 0.7, 0.3, 1),
            Anim.Sad, true, false, [Skill.GuitarSolo, Skill.Melt]),
        new("rem", "Rem", OllieBoard.Series.ReZero, 105, W(2, 1, 4, 1, 0.5, 1, 1.5, 1.5, 1),
            Anim.Grumpy, false, false, [Skill.MorningStar, Skill.OniForm]),

        // Three characters per supported series for now.
        new("tanjiro", "Tanjiro Kamado", OllieBoard.Series.Kimetsu, 100, W(3, 1.5, 3, 1, 1, 0.5, 2, 3, 1.2),
            Anim.Grumpy, false, false, [Skill.WaterBreathing, Skill.Hinokami]),
        new("zenitsu", "Agatsuma Zenitsu", OllieBoard.Series.Kimetsu, 110, W(2, 1, 0.7, 1.5, 3, 0.5, 1.5, 2, 1.2),
            Anim.Sad, true, false, [Skill.Thunderclap, Skill.ClingCry]),
        new("luffy", "Monkey D. Luffy", OllieBoard.Series.OnePiece, 115, W(3, 3.5, 0.3, 1, 1.5, 0.5, 3, 3, 1.2),
            Anim.Grumpy, false, true, [Skill.GomuPistol, Skill.Gear5]),
        new("zoro", "Roronoa Zoro", OllieBoard.Series.OnePiece, 90, W(2.5, 2, 0.5, 1, 4, 1, 0.5, 0.8, 1.2),
            Anim.Grumpy, false, false, [Skill.ThreeSword, Skill.GetLost]),
        new("nami", "Nami", OllieBoard.Series.OnePiece, 100, W(2.5, 1.5, 2.5, 1, 1, 1.5, 2, 2, 1.2),
            Anim.Grumpy, false, false, [Skill.ClimaTact, Skill.Berries]),
        new("eren", "Eren Yeager", OllieBoard.Series.AttackOnTitan, 110, W(3, 3, 0.7, 1, 0.7, 2, 1, 1.5, 1.2),
            Anim.Grumpy, false, false, [Skill.TitanShift, Skill.Tatakae]),
        new("mikasa", "Mikasa Ackerman", OllieBoard.Series.AttackOnTitan, 105, W(3, 2, 2, 1, 0.8, 0.5, 1, 2, 1.2),
            Anim.Grumpy, false, false, [Skill.OdmGear, Skill.Scarf]),
        new("levi", "Levi Ackerman", OllieBoard.Series.AttackOnTitan, 105, W(1.5, 1, 5, 1, 0.5, 1.5, 0.3, 0.7, 1.2),
            Anim.Grumpy, false, false, [Skill.SpinSlash, Skill.Cleaning]),
        new("yuji", "Yuji Itadori", OllieBoard.Series.JujutsuKaisen, 110, W(3, 3, 1, 1, 1, 0.5, 2.5, 3, 1.2),
            Anim.Grumpy, false, false, [Skill.DivergentFist, Skill.Sukuna]),
        new("gojo", "Satoru Gojo", OllieBoard.Series.JujutsuKaisen, 95, W(2.5, 2, 0.5, 2, 1, 0.3, 2.5, 2.5, 1.2),
            Anim.Grumpy, false, true, [Skill.InfiniteVoid, Skill.HollowPurple]),
        new("megumi", "Megumi Fushiguro", OllieBoard.Series.JujutsuKaisen, 95, W(2.5, 1, 2, 2, 1, 1, 0.5, 0.8, 1.2),
            Anim.Grumpy, false, false, [Skill.DivineDogs, Skill.RabbitEscape]),
        new("gon", "Gon Freecss", OllieBoard.Series.HunterXHunter, 115, W(3.5, 2.5, 1, 0.7, 1, 0.5, 3, 3, 1.2),
            Anim.Grumpy, false, false, [Skill.Jajanken, Skill.Fishing]),
        new("killua", "Killua Zoldyck", OllieBoard.Series.HunterXHunter, 120, W(3, 2.5, 0.7, 1.5, 1, 1, 1.5, 2, 1.2),
            Anim.Grumpy, false, true, [Skill.Godspeed, Skill.Chocolate]),
        new("kurapika", "Kurapika", OllieBoard.Series.HunterXHunter, 95, W(2, 1, 3, 1.5, 1, 1, 0.7, 1, 1.2),
            Anim.Grumpy, false, false, [Skill.ChainJail, Skill.ScarletEyes]),

        // A kitten in armor.
        new("catknight", "Cat Knight", OllieBoard.Series.CatKnight, 85, W(3, 2, 1.5, 1, 2, 0.5, 2.5, 2.5, 1.2),
            Anim.Grumpy, false, false, [Skill.KnightOath, Skill.ShieldBash]),

        // Penguinistrator: the Endministrator's playful chibi penguin meme form.
        new("gugugaga", "Gugugaga", OllieBoard.Series.Endfield, 75, W(3, 0.8, 1.8, 1.5, 2.5, 0.4, 3, 3, 1.3),
            Anim.Grumpy, false, true, [Skill.GuguChant, Skill.BellySlide]),

        // Playful political caricatures, same sketch skeleton as the anime cast.
        new("trump", "Donald Trump", OllieBoard.Series.Politics, 105, W(2.5, 3, 1, 1, 0.8, 1.2, 2.5, 3, 1.5),
            Anim.Grumpy, false, true, [Skill.Wall, Skill.Tariff]),
        new("putin", "Vladimir Putin", OllieBoard.Series.Politics, 100, W(2, 1.5, 3, 2, 1, 1, 1, 1, 1.2),
            Anim.Grumpy, false, false, [Skill.Judo, Skill.BearRide]),
        new("xi", "Tập Cận Bình", OllieBoard.Series.Politics, 90, W(2, 1, 4, 1.5, 0.8, 0.8, 1, 1.2, 1.2),
            Anim.Grumpy, false, false, [Skill.Parade, Skill.AiRobot]),
        new("kim", "Kim Jong Un", OllieBoard.Series.Politics, 85, W(2, 1.5, 1.5, 1, 0.7, 1, 3, 3, 1.5),
            Anim.Grumpy, false, false, [Skill.Applause, Skill.Missile]),
    ];
}
