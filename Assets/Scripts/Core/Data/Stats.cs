using System.Collections.Generic;

/// <summary>
/// The stat vocabulary: every id, what it is called, what it does, and how to print it.
///
/// One table, because five places need to agree about a stat — the block that stores
/// it, the JSON that authors it, the gear that grants it, the sheet that displays it
/// and the tooltip that explains it. Anything less than a single registry and a stat
/// ends up displayed under one name, authored under another, and applied by neither.
///
/// Ids are strings rather than an enum so a stat can be granted from item_data.json
/// without a client rebuild — the same reasoning as ItemEffect.action and
/// AbilityData.effect. StatBlock.Add reports an id nothing recognises rather than
/// dropping it, so a typo surfaces instead of quietly costing a player their bonus.
/// </summary>
public static class Stats
{
    // ── Vitals ────────────────────────────────────────────────────────────────
    public const string Health           = "health";
    public const string HealthMultiplier = "healthMultiplier";
    public const string Mana         = "mana";
    public const string Stamina      = "stamina";
    public const string HealthRegen  = "healthRegen";
    public const string ManaRegen    = "manaRegen";
    public const string StaminaRegen = "staminaRegen";

    // ── Offence ───────────────────────────────────────────────────────────────
    public const string MinHit                = "minHit";
    public const string MaxHit                = "maxHit";
    public const string MinHitMultiplier      = "minHitMultiplier";
    public const string MaxHitMultiplier      = "maxHitMultiplier";
    public const string CritChance            = "critChance";
    public const string CritMultiplier        = "critMultiplier";
    public const string AttackSpeed           = "attackSpeed";
    public const string AttackSpeedMultiplier = "attackSpeedMultiplier";

    // ── Defence ───────────────────────────────────────────────────────────────
    public const string Armor           = "armor";
    public const string ArmorMultiplier = "armorMultiplier";

    // ── Yield ─────────────────────────────────────────────────────────────────
    public const string DropRate           = "dropRate";
    public const string DropRateMultiplier = "dropRateMultiplier";

    // ── Particular to this game ───────────────────────────────────────────────
    public const string Momentum  = "momentum";
    public const string Diligence = "diligence";
    public const string Insight   = "insight";
    public const string Resonance = "resonance";
    public const string Tenacity  = "tenacity";
    public const string MoveSpeed = "moveSpeed";

    /// <summary>How a stat's number should be written.</summary>
    public enum Format
    {
        /// <summary>A plain quantity: 240.</summary>
        Whole,
        /// <summary>A quantity with decimals: 12.5.</summary>
        Decimal,
        /// <summary>A bonus fraction shown as a percentage: 0.25 → "+25%".</summary>
        Percent,
        /// <summary>A duration in seconds: "2.4s".</summary>
        Seconds,
        /// <summary>A rate: "1.5 / sec".</summary>
        PerSecond,
    }

    public class Info
    {
        public string Id;
        public string Name;
        public string Group;
        public string Description;
        public Format Display;

        /// <summary>True when a bigger number is worse, so the sheet can colour it.</summary>
        public bool LowerIsBetter;
    }

    public const string GroupVitals   = "VITALS";
    public const string GroupOffence  = "OFFENSE";
    public const string GroupDefence  = "DEFENSE";
    public const string GroupYield    = "YIELD";
    public const string GroupExplorer = "EXPLORER";

    /// <summary>
    /// Every stat, in display order. The descriptions are the hover text, so they say
    /// what the stat actually does mechanically rather than describing it in flavour —
    /// a player reading these is deciding what to spend a talent point on.
    /// </summary>
    public static readonly Info[] All =
    {
        // ── Vitals ────────────────────────────────────────────────────────────
        new Info { Id = Health, Name = "Health", Group = GroupVitals, Display = Format.Whole,
            Description = "How much damage you can take before you fall. Respawning costs you " +
                          "nothing but the walk back." },

        new Info { Id = HealthMultiplier, Name = "Health Bonus", Group = GroupVitals, Display = Format.Percent,
            Description = "Scales your total health. Stacks with the flat health from gear, so it " +
                          "is worth more the better armored you already are." },

        new Info { Id = Mana, Name = "Mana", Group = GroupVitals, Display = Format.Whole,
            Description = "Spent by arcane abilities. An ability you cannot pay for will not fire, " +
                          "so mana sets how often a caster can use their best options." },

        new Info { Id = Stamina, Name = "Stamina", Group = GroupVitals, Display = Format.Whole,
            Description = "Spent by physical abilities. The martial counterpart to mana — most " +
                          "characters lean on one of the two and ignore the other." },

        new Info { Id = HealthRegen, Name = "Health Regeneration", Group = GroupVitals, Display = Format.PerSecond,
            Description = "Health recovered every second, in and out of combat. The main reason a " +
                          "long unattended fight ends in your favour rather than the monster's." },

        new Info { Id = ManaRegen, Name = "Mana Regeneration", Group = GroupVitals, Display = Format.PerSecond,
            Description = "Mana recovered every second." },

        new Info { Id = StaminaRegen, Name = "Stamina Regeneration", Group = GroupVitals, Display = Format.PerSecond,
            Description = "Stamina recovered every second." },

        // ── Offence ───────────────────────────────────────────────────────────
        new Info { Id = MinHit, Name = "Minimum Hit", Group = GroupOffence, Display = Format.Decimal,
            Description = "The weakest a normal attack can land for. Raising it past your maximum " +
                          "hit is not wasted — the excess turns into critical chance, and once " +
                          "crits are certain, into critical damage." },

        new Info { Id = MaxHit, Name = "Maximum Hit", Group = GroupOffence, Display = Format.Decimal,
            Description = "The hardest a normal attack can land for. Every swing rolls somewhere " +
                          "between your minimum and this." },

        new Info { Id = MinHitMultiplier, Name = "Minimum Hit Bonus", Group = GroupOffence, Display = Format.Percent,
            Description = "Scales your minimum hit. Overflow past your maximum converts to " +
                          "critical chance, then to critical damage — so this never stops paying." },

        new Info { Id = MaxHitMultiplier, Name = "Maximum Hit Bonus", Group = GroupOffence, Display = Format.Percent,
            Description = "Scales your maximum hit, which also raises the ceiling your minimum hit " +
                          "can climb to before it starts converting." },

        new Info { Id = CritChance, Name = "Critical Chance", Group = GroupOffence, Display = Format.Percent,
            Description = "How often an attack lands as a critical hit." },

        new Info { Id = CritMultiplier, Name = "Critical Damage", Group = GroupOffence, Display = Format.Percent,
            Description = "Extra damage a critical hit deals on top of the normal roll." },

        new Info { Id = AttackSpeed, Name = "Attack Interval", Group = GroupOffence, Display = Format.Seconds,
            LowerIsBetter = true,
            Description = "Seconds between swings. Lower is faster." },

        new Info { Id = AttackSpeedMultiplier, Name = "Attack Speed", Group = GroupOffence, Display = Format.Percent,
            Description = "Shortens the gap between swings. +100% is twice as many attacks in the " +
                          "same time." },

        // ── Defence ───────────────────────────────────────────────────────────
        new Info { Id = Armor, Name = "Armor", Group = GroupDefence, Display = Format.Whole,
            Description = "Reduces incoming damage on a curve that never reaches zero, so armor " +
                          "always helps and never makes you invulnerable." },

        new Info { Id = ArmorMultiplier, Name = "Armor Bonus", Group = GroupDefence, Display = Format.Percent,
            Description = "Scales your armor before it is applied." },

        // ── Yield ─────────────────────────────────────────────────────────────
        new Info { Id = DropRate, Name = "Drop Quantity", Group = GroupYield, Display = Format.Decimal,
            Description = "Extra items per drop. Affects how much falls, not how rare it is." },

        new Info { Id = DropRateMultiplier, Name = "Drop Quantity Bonus", Group = GroupYield, Display = Format.Percent,
            Description = "Scales how much every drop yields. Rare things stay exactly as rare — " +
                          "for that, raise Insight." },

        // ── Particular to this game ───────────────────────────────────────────
        new Info { Id = Momentum, Name = "Momentum", Group = GroupExplorer, Display = Format.Percent,
            Description = "Bonus experience for staying on one activity. Builds over ten minutes " +
                          "of uninterrupted work and resets the moment you switch. Offline time " +
                          "always counts as full momentum — you were only doing the one thing." },

        new Info { Id = Diligence, Name = "Diligence", Group = GroupExplorer, Display = Format.Percent,
            Description = "How much you get done while logged out. This is the stat that matters " +
                          "most when you are not playing." },

        new Info { Id = Insight, Name = "Insight", Group = GroupExplorer, Display = Format.Percent,
            Description = "Your chance of the uncommon result — a rich seam, a rare drop, the find " +
                          "a node only sometimes gives up. Rarity, where Drop Quantity is volume." },

        new Info { Id = Resonance, Name = "Resonance", Group = GroupExplorer, Display = Format.Percent,
            Description = "How often your armor set bonuses trigger. Worthless without a set, and " +
                          "one of the strongest stats in the game with a full one." },

        new Info { Id = Tenacity, Name = "Tenacity", Group = GroupExplorer, Display = Format.Percent,
            Description = "Slows how fast your equipment wears out, so you spend less time and coin " +
                          "at the anvil and more time using the gear." },

        new Info { Id = MoveSpeed, Name = "Movement Speed", Group = GroupExplorer, Display = Format.Decimal,
            Description = "How fast you walk, in world units per second. It shortens every trip " +
                          "between a node, a corpse and the bank, which is most of what an idle " +
                          "character spends its time doing." },
    };

    private static Dictionary<string, Info> _byId;

    public static Info Get(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;

        if (_byId == null)
        {
            _byId = new Dictionary<string, Info>();
            foreach (var info in All) _byId[info.Id] = info;
        }
        return _byId.TryGetValue(id, out var found) ? found : null;
    }

    public static string NameOf(string id) => Get(id)?.Name ?? id;

    /// <summary>
    /// Older stat names, kept working.
    ///
    /// item_data.json shipped `maxHp` and `attackDamage` before this system existed.
    /// Both are migrated in the file, but content authored elsewhere — or a save from
    /// an older build — may still carry them, and an alias that quietly works beats a
    /// bonus that quietly does not.
    /// </summary>
    private static readonly Dictionary<string, string> Aliases = new()
    {
        { "maxHp",        Health },
        { "maxHealth",    Health },
        { "attackDamage", MaxHit },
        { "hpRegen",      HealthRegen },
    };

    /// <summary>Resolves an alias to the real stat id, or returns the id unchanged.</summary>
    public static string Canonical(string id)
    {
        if (string.IsNullOrEmpty(id)) return id;
        return Aliases.TryGetValue(id, out var real) ? real : id;
    }

    /// <summary>Formats a value the way its stat should read.</summary>
    public static string Render(string id, float value)
    {
        var info = Get(id);
        return (info?.Display ?? Format.Decimal) switch
        {
            Format.Whole     => $"{value:0}",
            Format.Percent   => $"{(value >= 0 ? "+" : "")}{value * 100f:0.#}%",
            Format.Seconds   => $"{value:0.00}s",
            Format.PerSecond => $"{value:0.##} / sec",
            _                => $"{value:0.##}",
        };
    }

    /// <summary>The distinct group headings, in display order.</summary>
    public static readonly string[] Groups =
    {
        GroupVitals, GroupOffence, GroupDefence, GroupYield, GroupExplorer,
    };
}
