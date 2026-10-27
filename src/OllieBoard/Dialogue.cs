using System.Text.Json;

namespace OllieBoard;

enum Line
{
    Happy, Bored, Tedious, Grumpy, Kick, Arrange, Click, Poke, Wake, NoIcons, Fail, Done,
    // Lines aimed at another character; "%@" is replaced by their short name.
    Greet, GreetBack, WakeOther, Hit, Sorry, Pranked, Chased, Comfort, Thanks, Together,
    // Agent mascots only; "{project}" is replaced by the session's project folder.
    AgentHello, AgentWorking, AgentDone, AgentNeedsYou, AgentBye,
    // Special moves (see Skill); "{app}" is replaced by the app the user is in.
    MindRead, MindReadIdle, ReadFriend, Thought, BloodBurst, Shrink, Grow, Flowers, MimicSpot, MimicEaten, MimicOut,
    Guitar, GuitarDone, Cheer, Melt, Reform, MorningStar, Oni, OniCalm,
    Wall, WallBuilt, Tariff, Judo, Bear, Parade, ParadeOrder, AiRobot, AiRobotDone, Applause, Missile, Boom,
    WaterBreathing, Hinokami, Thunderclap, ClingCry, GomuPistol, Gear5, ThreeSword, GetLost, LostAgain,
    ClimaTact, Berries, TitanShift, Tatakae, OdmGear, Scarf, SpinSlash, Cleaning, DivergentFist, Sukuna,
    InfiniteVoid, HollowPurple, DivineDogs, RabbitEscape, Jajanken, Fishing, FishCaught, Godspeed, Chocolate,
    ChainJail, ScarletEyes, KnightOath, ShieldBash, GuguChant, BellySlide,
}

enum Language { Vi, En, Ja }

static class Languages
{
    private const string Key = "language";

    public static string Title(this Language language) => language switch
    {
        Language.Vi => "Tiếng Việt",
        Language.En => "English",
        _ => "日本語",
    };

    public static Language Current
    {
        get => Enum.TryParse<Language>(Settings.GetString(Key), true, out var l) ? l : Language.Vi;
        set => Settings.SetString(Key, value.Key());
    }
}

/// Everything one character says in one language.
sealed class Lines
{
    public string ShortName = "";
    public Dictionary<Line, string[]> ByKind = new();
    /// Special greetings for a specific friend, keyed by their id.
    public Dictionary<string, string[]> PairGreet = new();
}

static class Dialogue
{
    private static Dictionary<Language, Dictionary<string, Lines>>? lines;
    private static Dictionary<Language, Dictionary<Skill, string>>? skillNames;

    private static void Load()
    {
        if (lines != null) return;
        lines = new();
        skillNames = new();
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "dialogue.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var lang in doc.RootElement.EnumerateObject())
        {
            if (!Enum.TryParse<Language>(lang.Name, true, out var language)) continue;
            var table = new Dictionary<string, Lines>();
            foreach (var character in lang.Value.GetProperty("lines").EnumerateObject())
            {
                var entry = new Lines { ShortName = character.Value.GetProperty("shortName").GetString() ?? "" };
                foreach (var kind in character.Value.GetProperty("lines").EnumerateObject())
                    if (Enum.TryParse<Line>(kind.Name, true, out var line))
                        entry.ByKind[line] = kind.Value.EnumerateArray().Select(v => v.GetString() ?? "").ToArray();
                foreach (var friend in character.Value.GetProperty("pairGreet").EnumerateObject())
                    entry.PairGreet[friend.Name] = friend.Value.EnumerateArray().Select(v => v.GetString() ?? "").ToArray();
                table[character.Name] = entry;
            }
            lines[language] = table;
            var names = new Dictionary<Skill, string>();
            foreach (var skill in lang.Value.GetProperty("skills").EnumerateObject())
                if (Enum.TryParse<Skill>(skill.Name, true, out var s)) names[s] = skill.Value.GetString() ?? "";
            skillNames[language] = names;
        }
    }

    public static string SkillName(Skill skill)
    {
        Load();
        return skillNames!.GetValueOrDefault(Languages.Current)?.GetValueOrDefault(skill) ?? skill.Key();
    }

    public static string DisplayName(this Character character, Language language)
    {
        Load();
        return character.FixedName ?? lines!.GetValueOrDefault(language)?.GetValueOrDefault(character.Id)?.ShortName ?? character.Name;
    }

    /// A random line of `kind` in the current language; "%@" becomes the partner's name in that language.
    public static string? Say(this Character character, Line kind, Character? partner = null, string project = "", string app = "")
    {
        Load();
        var language = Languages.Current;
        var own = lines!.GetValueOrDefault(language)?.GetValueOrDefault(character.DialogueId ?? character.Id);
        if (own == null) return null;
        var options = own.ByKind.GetValueOrDefault(kind) ?? [];
        if (kind == Line.Greet && partner != null && own.PairGreet.TryGetValue(partner.Id, out var special)) options = special;
        if (options.Length == 0) return null;
        var text = options[Random.Shared.Next(options.Length)];
        return text.Replace("%@", partner?.DisplayName(language) ?? "")
            .Replace("{project}", project.Length == 0 ? "project" : project)
            .Replace("{app}", app);
    }
}
