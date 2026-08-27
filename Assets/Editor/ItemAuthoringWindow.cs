using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Authors a new entry in item_data.json.
///
/// ══ WHY A WINDOW AND NOT A TEXT EDITOR ════════════════════════════════════════
///
/// Every field in an item is a reference to something else, and nothing in a text
/// editor checks any of them. An equipSlot that is not a real slot, a setId that no
/// set declares, a statBonus naming a stat that does not exist, an iconAddress
/// pointing at a Multiple-mode sheet — all of these parse as perfectly good JSON and
/// then do nothing at runtime, silently. The tin helmet shipped invisible for exactly
/// this reason, twice.
///
/// So this offers the references as lists rather than as free text, resolves the art
/// while you type, and refuses to write an item that cannot work.
///
/// ══ WHY IT APPENDS RATHER THAN REWRITES ═══════════════════════════════════════
///
/// item_data.json is hand-formatted: one item per line for the simple ones, four for a
/// piece of armour, with the columns lined up. Round-tripping it through JsonUtility
/// would reformat all seventy-six entries and turn a one-item change into a diff
/// nobody can review. This writes the new entry in the file's own style and touches
/// nothing else.
///
/// Menu: Idle Explorers → New Item...
/// </summary>
public class ItemAuthoringWindow : EditorWindow
{
    private const string ITEM_DATA = "Assets/StreamingAssets/item_data.json";
    private const string SET_DATA  = "Assets/StreamingAssets/set_data.json";
    private const string SKILL_DATA = "Assets/StreamingAssets/skill_data.json";

    [System.Serializable] private class ItemFile  { public ItemData[]    items; }
    [System.Serializable] private class SetFile   { public ItemSetData[] sets; }
    [System.Serializable] private class SkillFile { public SkillData[]   skills; }

    [MenuItem("Idle Explorers/New Item...")]
    public static void Open()
    {
        var window = GetWindow<ItemAuthoringWindow>(utility: false, title: "New Item");
        window.minSize = new Vector2(520f, 640f);
        window.Reload();
    }

    // ── What is being authored ────────────────────────────────────────────────

    private string _id          = "";
    private string _name        = "";
    private string _description = "";
    private string _iconAddress = "";

    private bool _stackable = true;
    private int  _levelReq;
    private int  _sourceSkill;      // index into _skillIds

    private int    _equipSlot;      // index into _slotIds
    private string _equipSprite = "";
    private int    _setId;          // index into _setIds
    private int    _maxDurability;

    // ── Weapon ────────────────────────────────────────────────────────────────
    //
    // Written only when a weapon type is chosen, so an ordinary chestplate does not
    // acquire an attackRange of zero -- which WeaponProfile would read as "authored,
    // and it is nothing" rather than "not a weapon".
    private int    _weaponType;     // index into WeaponTypes
    private float  _attackRange;
    private float  _attackSpeed;
    private float  _damageMin;
    private float  _damageMax;
    private int    _projectiles;
    private bool   _twoHanded;

    /// <summary>
    /// The three states a weapon field can be in, in the order the JSON writes them.
    ///
    /// "" first, so the default for a new item is NOT a weapon. An item that became a
    /// weapon by being left alone is how a tabard ends up with a swing timer.
    /// </summary>
    private static readonly string[] WeaponTypes = { "", "melee", "ranged" };
    private static readonly string[] WeaponLabels = { "not a weapon", "melee", "ranged" };

    private readonly List<EffectDraft> _effects = new();

    private class EffectDraft
    {
        public int    Trigger;
        public int    Action;
        public float  Chance    = 1f;
        public float  Magnitude = 1f;
        public string Param     = "";
        public string EquipText = "";
    }

    // ── The things an item can refer to ───────────────────────────────────────

    private string[] _skillIds, _skillLabels;
    private string[] _slotIds,  _slotLabels;
    private string[] _setIds,   _setLabels;
    private string[] _statIds,  _statLabels;
    private string[] _itemIds;                  // for "start from"
    private HashSet<string> _existingIds = new();
    private Dictionary<string, string[]> _setMembers = new();

    private int     _startFrom;
    private Vector2 _scroll;
    private string  _lastWritten;

    /// <summary>
    /// Which actions each trigger can carry.
    ///
    /// Taken from the two places that dispatch them — ItemEffectResolver.Apply for
    /// onConsume, and FireProc plus the equipment readers for the rest. An action
    /// under the wrong trigger is never reached, and looks from the outside exactly
    /// like an unlucky proc chance.
    /// </summary>
    private static readonly Dictionary<string, string[]> ActionsByTrigger = new()
    {
        ["onEquipPassive"] = new[] { "statBonus" },
        ["onConsume"]      = new[] { "heal", "damageSelf", "grantAfkTime", "changeClass",
                                     "changeAppearance", "resetSkills", "damageEquipment" },
        ["onAbilityUse"]   = new[] { "castEffect" },
        ["onGather"]       = new[] { "bonusXp", "extraLoot" },
        ["onCraft"]        = new[] { "doubleOutput" },
    };

    private static readonly string[] Triggers =
        { "onEquipPassive", "onConsume", "onAbilityUse", "onGather", "onCraft" };

    // ── Loading what already exists ───────────────────────────────────────────

    private void Reload()
    {
        var items = LoadArray<ItemFile>(ITEM_DATA, "items")?.items;
        _existingIds = new HashSet<string>();
        var ids = new List<string> { "(blank)" };

        if (items != null)
            foreach (var item in items)
            {
                if (item == null || string.IsNullOrEmpty(item.id)) continue;
                _existingIds.Add(item.id);
                ids.Add(item.id);
            }

        _itemIds = ids.ToArray();

        var sets = LoadArray<SetFile>(SET_DATA, "sets")?.sets;
        var setIds    = new List<string> { "" };
        var setLabels = new List<string> { "(none)" };
        _setMembers = new Dictionary<string, string[]>();

        if (sets != null)
            foreach (var set in sets)
            {
                if (set == null || string.IsNullOrEmpty(set.id)) continue;
                setIds.Add(set.id);
                setLabels.Add($"{set.name}  ({set.id})");
                _setMembers[set.id] = set.itemIds ?? System.Array.Empty<string>();
            }

        _setIds    = setIds.ToArray();
        _setLabels = setLabels.ToArray();

        var skills = LoadArray<SkillFile>(SKILL_DATA, "skills")?.skills;
        var skillIds    = new List<string> { "" };
        var skillLabels = new List<string> { "(none)" };

        if (skills != null)
            foreach (var skill in skills)
            {
                if (skill == null || string.IsNullOrEmpty(skill.id)) continue;
                skillIds.Add(skill.id);
                skillLabels.Add($"{skill.DisplayName}  ({skill.id})");
            }

        // Combat is the source of every monster drop and is not a trainable skill, so
        // it is not in skill_data.json — but forty items name it.
        skillIds.Add("combat");
        skillLabels.Add("Combat  (combat)");

        _skillIds    = skillIds.ToArray();
        _skillLabels = skillLabels.ToArray();

        var slotIds    = new List<string> { "" };
        var slotLabels = new List<string> { "(not equippable)" };

        foreach (var slot in EquipmentSlots.All)
        {
            slotIds.Add(slot.SlotId);
            slotLabels.Add($"{slot.DisplayName}  ({slot.SlotId})" +
                           (slot.RendersOnCharacter ? "" : "  — no art layer"));
        }

        _slotIds    = slotIds.ToArray();
        _slotLabels = slotLabels.ToArray();

        var statIds    = new List<string>();
        var statLabels = new List<string>();

        foreach (var stat in Stats.All)
        {
            statIds.Add(stat.Id);
            statLabels.Add($"{stat.Group}/{stat.Name}  ({stat.Id})");
        }

        _statIds    = statIds.ToArray();
        _statLabels = statLabels.ToArray();
    }

    private static T LoadArray<T>(string path, string field) where T : class
    {
        if (!File.Exists(path)) return null;

        string raw = File.ReadAllText(path).Trim();
        if (!raw.StartsWith("[")) return null;

        // JsonUtility cannot parse a bare array, so it is wrapped in the object shape
        // the loader expects. The same trick ContentManager and the art validator use.
        try   { return JsonUtility.FromJson<T>("{\"" + field + "\":" + raw + "}"); }
        catch { return null; }
    }

    // ── Drawing ───────────────────────────────────────────────────────────────

    private void OnGUI()
    {
        if (_slotIds == null) Reload();

        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        EditorGUILayout.HelpBox(
            "Writes one new entry to item_data.json, in the file's own formatting. " +
            "Nothing else in the file is touched.", MessageType.None);

        DrawStartFrom();
        DrawIdentity();
        DrawBasics();
        DrawEquipment();
        DrawEffects();

        var problems = Validate();
        DrawProblems(problems);
        DrawPreview();
        DrawWriteButton(problems);

        EditorGUILayout.EndScrollView();
    }

    private void DrawStartFrom()
    {
        Section("Start from");

        using (new EditorGUILayout.HorizontalScope())
        {
            int chosen = EditorGUILayout.Popup("Copy an existing item", _startFrom, _itemIds);
            if (chosen != _startFrom)
            {
                _startFrom = chosen;
                if (chosen > 0) CopyFrom(_itemIds[chosen]);
            }

            if (GUILayout.Button("Reload lists", GUILayout.Width(110f))) Reload();
        }

        EditorGUILayout.LabelField(
            "A six-piece armour set is five near-identical entries. Copy one, change " +
            "the slot and the name.", EditorStyles.miniLabel);
    }

    private void CopyFrom(string itemId)
    {
        var items = LoadArray<ItemFile>(ITEM_DATA, "items")?.items;
        if (items == null) return;

        foreach (var item in items)
        {
            if (item == null || item.id != itemId) continue;

            // The id is deliberately NOT copied. Two items sharing one is the mistake
            // this window exists to make impossible, and leaving it blank makes the
            // next step obvious.
            _id            = "";
            _name          = item.name;
            _description   = item.description;
            _iconAddress   = item.iconAddress;
            _stackable     = item.stackable;
            _levelReq      = item.levelReq;
            _sourceSkill   = IndexOf(_skillIds, item.sourceSkill);
            _equipSlot     = IndexOf(_slotIds,  item.equipSlot);
            _equipSprite   = item.equipSpriteAddress ?? "";
            _setId         = IndexOf(_setIds,   item.setId);
            _maxDurability = item.maxDurability;

            // Copied too, because the reason to copy an item is usually that the new
            // one is nearly the same -- and the sixth dagger in a tier having lost its
            // reach because the window did not carry it over is a bug found in a
            // playtest rather than here.
            _weaponType  = IndexOf(WeaponTypes, item.weaponType ?? "");
            _attackRange = item.attackRange;
            _attackSpeed = item.attackSpeedSeconds;
            _damageMin   = item.damageMin;
            _damageMax   = item.damageMax;
            _projectiles = item.projectilesPerShot;
            _twoHanded   = item.twoHanded;

            _effects.Clear();
            if (item.effects != null)
                foreach (var effect in item.effects)
                {
                    if (effect == null) continue;

                    int trigger = IndexOf(Triggers, effect.trigger);
                    if (trigger < 0) trigger = 0;

                    _effects.Add(new EffectDraft
                    {
                        Trigger   = trigger,
                        Action    = IndexOf(ActionsFor(trigger), effect.action),
                        Chance    = effect.chance,
                        Magnitude = effect.magnitude,
                        Param     = effect.param ?? "",
                        EquipText = effect.equipText ?? "",
                    });
                }

            GUI.FocusControl(null);
            return;
        }
    }

    private void DrawIdentity()
    {
        Section("Identity");

        using (new EditorGUILayout.HorizontalScope())
        {
            _id = EditorGUILayout.TextField("id", _id);

            using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(_name)))
                if (GUILayout.Button("From name", GUILayout.Width(90f)))
                {
                    _id = Slug(_name);
                    GUI.FocusControl(null);
                }
        }

        _name = EditorGUILayout.TextField("name", _name);

        EditorGUILayout.LabelField("description");
        _description = EditorGUILayout.TextArea(_description, GUILayout.Height(48f));
    }

    private void DrawBasics()
    {
        Section("Basics");

        _stackable   = EditorGUILayout.Toggle("stackable", _stackable);
        _levelReq    = Mathf.Max(0, EditorGUILayout.IntField("levelReq", _levelReq));
        _sourceSkill = EditorGUILayout.Popup("sourceSkill", Mathf.Max(0, _sourceSkill), _skillLabels);

        EditorGUILayout.Space(4f);
        _iconAddress = EditorGUILayout.TextField("iconAddress", _iconAddress);
        DrawArtStatus(_iconAddress, "Inventory icon");
    }

    private void DrawEquipment()
    {
        Section("Equipment");

        _equipSlot = EditorGUILayout.Popup("equipSlot", Mathf.Max(0, _equipSlot), _slotLabels);

        if (!IsEquippable)
        {
            EditorGUILayout.LabelField("Not equippable — the fields below are not written.",
                                        EditorStyles.miniLabel);
            return;
        }

        _equipSprite = EditorGUILayout.TextField("equipSpriteAddress", _equipSprite);
        DrawArtStatus(_equipSprite, "Worn on the character");

        _setId = EditorGUILayout.Popup("setId", Mathf.Max(0, _setId), _setLabels);
        _maxDurability = Mathf.Max(0, EditorGUILayout.IntField("maxDurability", _maxDurability));
        EditorGUILayout.LabelField("0 means indestructible.", EditorStyles.miniLabel);

        DrawWeapon();
    }

    /// <summary>
    /// The weapon fields, shown only for the two hand slots.
    ///
    /// Hidden elsewhere rather than merely ignored: a damage field on a helmet is an
    /// invitation, and the resolver would not read it -- so an author would fill it
    /// in, ship it, and discover months later that the helmet never hit anything.
    /// </summary>
    private void DrawWeapon()
    {
        if (SlotId != "mainhand" && SlotId != "offhand") return;

        EditorGUILayout.Space(4f);

        _weaponType = EditorGUILayout.Popup("weaponType", Mathf.Clamp(_weaponType, 0, WeaponTypes.Length - 1),
                                             WeaponLabels);

        if (WeaponType.Length == 0)
        {
            EditorGUILayout.LabelField("A shield or a torch — no weapon fields are written.",
                                        EditorStyles.miniLabel);
            return;
        }

        _attackRange = EditorGUILayout.FloatField("attackRange", _attackRange);
        _attackSpeed = EditorGUILayout.FloatField("attackSpeedSeconds", _attackSpeed);

        _damageMin = EditorGUILayout.FloatField("damageMin", _damageMin);
        _damageMax = EditorGUILayout.FloatField("damageMax", _damageMax);

        if (WeaponType == "ranged")
        {
            _projectiles = EditorGUILayout.IntField("projectilesPerShot", _projectiles);
            EditorGUILayout.LabelField("0 or 1 fires one. The Trisong Bow is three.",
                                        EditorStyles.miniLabel);
        }

        _twoHanded = EditorGUILayout.Toggle("twoHanded", _twoHanded);

        if (_twoHanded)
        {
            EditorGUILayout.LabelField("Equipping this takes the off hand off.",
                                        EditorStyles.miniLabel);
        }

        // The clamps WeaponProfile applies anyway, stated here so an author sees the
        // number they will actually get rather than the number they typed.
        if (_attackRange > 0f && _attackRange > IdleExplorers.Rules.WeaponProfile.MaxRange)
        {
            EditorGUILayout.HelpBox(
                $"Reach is capped at {IdleExplorers.Rules.WeaponProfile.MaxRange} in play.",
                MessageType.Info);
        }

        if (_attackSpeed > 0f && _attackSpeed < IdleExplorers.Rules.WeaponProfile.MinAttackSeconds)
        {
            EditorGUILayout.HelpBox(
                $"Swings are floored at {IdleExplorers.Rules.WeaponProfile.MinAttackSeconds}s in play — " +
                "below that a speed stops describing an animation and becomes a damage multiplier.",
                MessageType.Info);
        }
    }

    private string WeaponType => WeaponTypes[Mathf.Clamp(_weaponType, 0, WeaponTypes.Length - 1)];

    /// <summary>
    /// What is wrong with the weapon, if anything.
    ///
    /// ══ WHY THESE ARE ERRORS AND NOT WARNINGS ═════════════════════════════════
    ///
    /// Because each one produces a weapon that is silently useless rather than
    /// obviously broken. A bow with no range never closes to fire; a weapon whose
    /// maximum damage is below its minimum rolls an empty band. Neither throws, and
    /// neither looks wrong in the JSON -- they look like a weapon that turned out to
    /// be bad, which is a balance conversation rather than a bug report.
    /// </summary>
    private List<Problem> WeaponProblems()
    {
        var problems = new List<Problem>();

        bool inHand = SlotId == "mainhand" || SlotId == "offhand";

        if (!inHand)
        {
            // A weapon type on a helmet is not written -- see DrawWeapon -- but an
            // author who set one before changing the slot deserves to be told rather
            // than left wondering where it went.
            if (WeaponType.Length > 0)
            {
                problems.Add(new Problem(true, $"weaponType is set but the slot is '{SlotId}'. " +
                                                "Only mainhand and offhand carry weapons, so it will " +
                                                "not be written."));
            }

            return problems;
        }

        if (WeaponType.Length == 0) return problems;

        if (_damageMax > 0f && _damageMax < _damageMin)
        {
            problems.Add(new Problem(true, $"damageMax ({Number(_damageMax)}) is below damageMin " +
                                            $"({Number(_damageMin)}). The band is empty and every " +
                                            "swing rolls the minimum."));
        }

        if (WeaponType == "ranged" && _attackRange <= 0f)
        {
            problems.Add(new Problem(true, "A ranged weapon with no attackRange falls back to the " +
                                            "unarmed reach, so it walks into melee to shoot."));
        }

        if (_projectiles > 1 && WeaponType != "ranged")
        {
            problems.Add(new Problem(true, "projectilesPerShot only fires from a ranged weapon. " +
                                            "It will not be written."));
        }

        if (_projectiles > IdleExplorers.Rules.WeaponProfile.MaxProjectiles)
        {
            problems.Add(new Problem(false, $"projectilesPerShot is capped at " +
                                             $"{IdleExplorers.Rules.WeaponProfile.MaxProjectiles} in play."));
        }

        if (_twoHanded && SlotId == "offhand")
        {
            problems.Add(new Problem(true, "An off-hand item cannot be two-handed. Nothing could ever " +
                                            "hold it, because equipping a two-hander empties this slot."));
        }

        if (_attackSpeed <= 0f)
        {
            problems.Add(new Problem(false, "No attackSpeedSeconds, so the weapon swings at the " +
                                             "character's own speed. Intended for a plain weapon, and " +
                                             "a mistake for anything meant to feel heavy or quick."));
        }

        return problems;
    }

    /// <summary>
    /// The weapon fields worth writing, or nothing.
    ///
    /// ══ WHY ZEROES ARE OMITTED RATHER THAN WRITTEN ════════════════════════════
    ///
    /// Because WeaponProfile distinguishes "not authored" from "authored as zero", and
    /// the two mean opposite things. An unauthored range falls back to the unarmed
    /// reach; a range of zero would mean "occupy the same point as the monster", which
    /// the navmesh will never satisfy and the attack loop would wait for forever.
    ///
    /// So a field goes in only when somebody actually set it.
    /// </summary>
    private List<string> WeaponFields()
    {
        var parts = new List<string>();

        if (SlotId != "mainhand" && SlotId != "offhand") return parts;
        if (WeaponType.Length == 0 && !_twoHanded)       return parts;

        if (WeaponType.Length > 0) parts.Add($"\"weaponType\": {Json(WeaponType)}");

        if (_attackRange > 0f) parts.Add($"\"attackRange\": {Number(_attackRange)}");
        if (_attackSpeed > 0f) parts.Add($"\"attackSpeedSeconds\": {Number(_attackSpeed)}");

        if (_damageMin > 0f) parts.Add($"\"damageMin\": {Number(_damageMin)}");
        if (_damageMax > 0f) parts.Add($"\"damageMax\": {Number(_damageMax)}");

        if (WeaponType == "ranged" && _projectiles > 1)
            parts.Add($"\"projectilesPerShot\": {_projectiles}");

        // Only when true. A "twoHanded": false on every dagger is noise, and the field
        // defaults to false anyway.
        if (_twoHanded) parts.Add("\"twoHanded\": true");

        return parts;
    }

    /// <summary>
    /// A float, in a form Unity reads back the same way on every machine.
    ///
    /// InvariantCulture explicitly: a decimal comma from a European editor produces
    /// JSON that parses as two fields, and the failure lands on somebody else.
    /// </summary>
    private static string Number(float value) =>
        value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Says whether an art address actually resolves, using the same loader the game
    /// does — so "it looks right" and "it works" cannot disagree.
    /// </summary>
    private void DrawArtStatus(string address, string what)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            EditorGUILayout.LabelField(" ", $"{what}: none.", EditorStyles.miniLabel);
            return;
        }

        var sprite = SpriteLoader.Load(address);
        if (sprite != null)
        {
            EditorGUILayout.LabelField(" ", $"{what}: resolves to '{sprite.name}'.",
                                        EditorStyles.miniLabel);
            return;
        }

        var subs = SpriteLoader.SubSpriteNames(address);
        string hint = subs != null && subs.Length > 0
            ? $"It is a Multiple-mode sheet — name a sub-sprite, e.g. {address}#{subs[0]}"
            : "Nothing at that Resources path.";

        EditorGUILayout.HelpBox($"{what} does not resolve. {hint}", MessageType.Warning);

        if (GUILayout.Button("Recheck art (clears the sprite cache)"))
        {
            SpriteLoader.Clear();
            Repaint();
        }
    }

    private void DrawEffects()
    {
        Section("Effects");

        for (int i = 0; i < _effects.Count; i++)
        {
            var effect = _effects[i];

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField($"Effect {i + 1}", EditorStyles.boldLabel);
                    if (GUILayout.Button("Remove", GUILayout.Width(70f)))
                    {
                        _effects.RemoveAt(i);
                        return;
                    }
                }

                int trigger = EditorGUILayout.Popup("trigger", effect.Trigger, Triggers);
                if (trigger != effect.Trigger)
                {
                    // The action list belongs to the trigger, so an index carried over
                    // would point at whatever happens to sit there in the new list.
                    effect.Trigger = trigger;
                    effect.Action  = 0;
                }

                var actions = ActionsFor(effect.Trigger);
                effect.Action = EditorGUILayout.Popup("action",
                                                       Mathf.Clamp(effect.Action, 0, actions.Length - 1),
                                                       actions);

                string action = actions[Mathf.Clamp(effect.Action, 0, actions.Length - 1)];

                effect.Chance    = Mathf.Clamp01(EditorGUILayout.FloatField("chance (0-1)", effect.Chance));
                effect.Magnitude = EditorGUILayout.FloatField("magnitude", effect.Magnitude);

                DrawParam(effect, action);

                effect.EquipText = EditorGUILayout.TextField("equipText", effect.EquipText);
                EditorGUILayout.LabelField("Shown verbatim in the tooltip.", EditorStyles.miniLabel);
            }
        }

        if (GUILayout.Button("Add effect")) _effects.Add(new EffectDraft());
    }

    /// <summary>
    /// param means something different per action, and nothing warns when it is the
    /// wrong kind of id. Offering the right list is most of the value of this window.
    /// </summary>
    private void DrawParam(EffectDraft effect, string action)
    {
        switch (action)
        {
            case "statBonus":
            {
                int current = IndexOf(_statIds, effect.Param);
                if (current < 0) current = 0;

                int chosen = EditorGUILayout.Popup("param (stat)", current, _statLabels);
                effect.Param = _statIds.Length > 0 ? _statIds[chosen] : "";
                break;
            }

            case "bonusXp":
            case "doubleOutput":
            {
                int current = IndexOf(_skillIds, effect.Param);
                if (current < 0) current = 0;

                int chosen = EditorGUILayout.Popup("param (skill)", current, _skillLabels);
                effect.Param = _skillIds[chosen];
                EditorGUILayout.LabelField("Empty applies to every skill.", EditorStyles.miniLabel);
                break;
            }

            case "castEffect":
                effect.Param = EditorGUILayout.TextField("param (vfx id)", effect.Param);
                break;

            case "extraLoot":
                effect.Param = EditorGUILayout.TextField("param (item id)", effect.Param);
                break;

            default:
                EditorGUILayout.LabelField("param", "not used by this action");
                effect.Param = "";
                break;
        }
    }

    // ── Validation ────────────────────────────────────────────────────────────

    private readonly struct Problem
    {
        public readonly bool   Blocking;
        public readonly string Text;

        public Problem(bool blocking, string text) { Blocking = blocking; Text = text; }
    }

    private bool IsEquippable => _equipSlot > 0 && _equipSlot < _slotIds.Length;

    private string SlotId    => IsEquippable ? _slotIds[_equipSlot] : "";
    private string SetId     => _setId > 0 && _setId < _setIds.Length ? _setIds[_setId] : "";
    private string SkillId   => _sourceSkill > 0 && _sourceSkill < _skillIds.Length
                                    ? _skillIds[_sourceSkill] : "";

    private List<Problem> Validate()
    {
        var problems = new List<Problem>();

        if (string.IsNullOrWhiteSpace(_id))
            problems.Add(new Problem(true, "id is empty."));
        else if (!Regex.IsMatch(_id, "^[a-z0-9_]+$"))
            problems.Add(new Problem(true, $"id '{_id}' must be lower case letters, digits and " +
                                            "underscores — every other id in the file is."));
        else if (_existingIds.Contains(_id))
            problems.Add(new Problem(true, $"id '{_id}' is already taken. GetItem returns the FIRST " +
                                            "match, so a duplicate makes one of the two unreachable."));

        if (string.IsNullOrWhiteSpace(_name))
            problems.Add(new Problem(true, "name is empty — it is what the player sees everywhere."));

        if (string.IsNullOrWhiteSpace(_description))
            problems.Add(new Problem(false, "No description. The tooltip will have a blank line in it."));

        if (!IsEquippable)
        {
            if (SetId != "")
                problems.Add(new Problem(true, "setId is set on something that cannot be worn. " +
                                                "Set bonuses count WORN pieces, so this piece could " +
                                                "never contribute."));

            if (_maxDurability > 0)
                problems.Add(new Problem(true, "maxDurability is set on something that cannot be " +
                                                "worn. Only equipment wears out."));
        }
        else
        {
            var slot = EquipmentSlots.Get(SlotId);

            if (slot != null && !slot.RendersOnCharacter && !string.IsNullOrWhiteSpace(_equipSprite))
                problems.Add(new Problem(false, $"Slot '{SlotId}' has no layer on the SPUM rig, so " +
                                                 "equipSpriteAddress will not be drawn. The item still " +
                                                 "equips and still carries its stats."));

            if (slot != null && slot.RendersOnCharacter && string.IsNullOrWhiteSpace(_equipSprite))
                problems.Add(new Problem(false, "No equipSpriteAddress — this equips without changing " +
                                                 "how the character looks."));

            if (SetId != "" && _setMembers.TryGetValue(SetId, out var members))
            {
                bool listed = false;
                foreach (var member in members) if (member == _id) listed = true;

                if (!listed)
                    problems.Add(new Problem(false, $"set_data.json's '{SetId}' does not list '{_id}' " +
                                                     "in its itemIds. The set will not count this piece " +
                                                     "until it does — add it there by hand."));
            }
        }

        if (_stackable && IsEquippable)
            problems.Add(new Problem(false, "Stackable equipment. Durability is stored per SLOT, so a " +
                                             "stack of worn pieces cannot each remember their own wear."));

        problems.AddRange(WeaponProblems());

        for (int i = 0; i < _effects.Count; i++)
        {
            var effect  = _effects[i];
            var actions = ActionsFor(effect.Trigger);
            string action = actions[Mathf.Clamp(effect.Action, 0, actions.Length - 1)];
            string where  = $"Effect {i + 1} ({action})";

            if (effect.Chance <= 0f)
                problems.Add(new Problem(true, $"{where}: chance is 0, so it can never fire."));

            if (action == "statBonus" && string.IsNullOrWhiteSpace(effect.Param))
                problems.Add(new Problem(true, $"{where}: needs a stat in param."));

            if (action == "statBonus" && Mathf.Approximately(effect.Magnitude, 0f))
                problems.Add(new Problem(false, $"{where}: magnitude is 0, so it grants nothing."));

            if (action == "statBonus" && Triggers[effect.Trigger] == "onEquipPassive" && !IsEquippable)
                problems.Add(new Problem(true, $"{where}: an onEquipPassive effect on something that " +
                                                "cannot be equipped never runs."));

            if (action == "extraLoot")
                problems.Add(new Problem(false, $"{where}: extraLoot is filtered by the same param it " +
                                                 "grants, so it only fires when the gathered SKILL id " +
                                                 "equals the ITEM id. Nothing ships using it, and as " +
                                                 "written it cannot fire. See ItemEffectResolver." ));

            if (action == "castEffect" && string.IsNullOrWhiteSpace(effect.Param))
                problems.Add(new Problem(true, $"{where}: needs a VFX id in param."));

            if (string.IsNullOrWhiteSpace(effect.EquipText) && IsEquippable)
                problems.Add(new Problem(false, $"{where}: no equipText, so the tooltip says nothing " +
                                                 "about it."));
        }

        // A warning rather than an error: an item with no art is authorable, playable
        // and testable, and blocking on a sprite would make adding a recipe input a
        // two-step job. It just draws a placeholder, and nobody notices until a
        // playtest -- which is what this line exists to prevent.
        if (string.IsNullOrWhiteSpace(_iconAddress) && !string.IsNullOrWhiteSpace(_id) &&
            !IconLibrarySetup.HasIconFor(_id))
        {
            problems.Add(new Problem(false, $"No iconAddress, and IconLibrarySetup has no entry for " +
                                             $"'{_id}' — the inventory slot will be blank. Add one to " +
                                             "ItemIconNames and rebuild the icon library."));
        }

        return problems;
    }

    private void DrawProblems(List<Problem> problems)
    {
        Section("Checks");

        if (problems.Count == 0)
        {
            EditorGUILayout.HelpBox("Nothing to report.", MessageType.Info);
            return;
        }

        foreach (var problem in problems)
            EditorGUILayout.HelpBox(problem.Text,
                                     problem.Blocking ? MessageType.Error : MessageType.Warning);
    }

    // ── Writing ───────────────────────────────────────────────────────────────

    private void DrawPreview()
    {
        Section("Will be appended");

        var style = new GUIStyle(EditorStyles.textArea) { wordWrap = false, fontSize = 11 };
        EditorGUILayout.SelectableLabel(BuildEntry(), style, GUILayout.Height(84f));
    }

    private void DrawWriteButton(List<Problem> problems)
    {
        bool blocked = false;
        foreach (var problem in problems) if (problem.Blocking) blocked = true;

        EditorGUILayout.Space(6f);

        using (new EditorGUI.DisabledScope(blocked))
            if (GUILayout.Button(blocked ? "Fix the errors above first" : "Add to item_data.json",
                                  GUILayout.Height(30f)))
                Write();

        if (!string.IsNullOrEmpty(_lastWritten))
            EditorGUILayout.HelpBox($"Added '{_lastWritten}'. The id field has been cleared so the " +
                                     "next piece of a set can be authored straight away.",
                                     MessageType.Info);
    }

    /// <summary>
    /// The entry, in the file's own layout: one line for the basics, then a line each
    /// for the equipment fields, the set fields and the effects — only when there is
    /// something to put on them.
    /// </summary>
    private string BuildEntry()
    {
        var text = new StringBuilder();

        text.Append("  { ");
        text.Append($"\"id\": {Json(_id)}, ");
        text.Append($"\"name\": {Json(_name)}, ");
        text.Append($"\"description\": {Json(_description)}, ");
        text.Append($"\"iconAddress\": {Json(_iconAddress)}, ");
        text.Append($"\"stackable\": {(_stackable ? "true" : "false")}, ");
        text.Append($"\"levelReq\": {_levelReq}, ");
        text.Append($"\"sourceSkill\": {Json(SkillId)}");

        bool more = IsEquippable || _effects.Count > 0;
        text.Append(more ? ",\n" : " }");

        if (IsEquippable)
        {
            text.Append($"    \"equipSlot\": {Json(SlotId)}");
            if (!string.IsNullOrWhiteSpace(_equipSprite))
                text.Append($", \"equipSpriteAddress\": {Json(_equipSprite)}");

            var weapon = WeaponFields();

            bool trailing = SetId != "" || _maxDurability > 0 || weapon.Count > 0 || _effects.Count > 0;
            text.Append(trailing ? ",\n" : " }");

            if (SetId != "" || _maxDurability > 0)
            {
                var parts = new List<string>();
                if (SetId != "")        parts.Add($"\"setId\": {Json(SetId)}");
                if (_maxDurability > 0) parts.Add($"\"maxDurability\": {_maxDurability}");

                text.Append("    " + string.Join(", ", parts));
                text.Append(weapon.Count > 0 || _effects.Count > 0 ? ",\n" : " }");
            }

            if (weapon.Count > 0)
            {
                text.Append("    " + string.Join(", ", weapon));
                text.Append(_effects.Count > 0 ? ",\n" : " }");
            }
        }

        if (_effects.Count > 0)
        {
            var written = new List<string>();

            foreach (var effect in _effects)
            {
                var actions = ActionsFor(effect.Trigger);
                string action = actions[Mathf.Clamp(effect.Action, 0, actions.Length - 1)];

                var parts = new List<string>
                {
                    $"\"trigger\": {Json(Triggers[effect.Trigger])}",
                    $"\"action\": {Json(action)}",
                };

                // chance is 1 by default in the model, so writing it only when it is
                // not 1 keeps the file reading the way the rest of it does.
                if (!Mathf.Approximately(effect.Chance, 1f))
                    parts.Add($"\"chance\": {effect.Chance:0.###}");

                if (!string.IsNullOrWhiteSpace(effect.Param))
                    parts.Add($"\"param\": {Json(effect.Param)}");

                parts.Add($"\"magnitude\": {effect.Magnitude:0.###}");

                if (!string.IsNullOrWhiteSpace(effect.EquipText))
                    parts.Add($"\"equipText\": {Json(effect.EquipText)}");

                written.Add("{ " + string.Join(", ", parts) + " }");
            }

            text.Append($"    \"effects\": [ {string.Join(", ", written)} ] }}");
        }

        return text.ToString();
    }

    private void Write()
    {
        if (!File.Exists(ITEM_DATA))
        {
            EditorUtility.DisplayDialog("No item data", $"Expected {ITEM_DATA}.", "OK");
            return;
        }

        string raw = File.ReadAllText(ITEM_DATA);

        int close = raw.LastIndexOf(']');
        if (close < 0)
        {
            EditorUtility.DisplayDialog("Cannot append",
                $"{ITEM_DATA} does not end in a ']'. Refusing to guess where the array ends.", "OK");
            return;
        }

        // Everything before the closing bracket, with the whitespace trimmed off so the
        // comma lands against the previous entry rather than on a line of its own.
        string body = raw.Substring(0, close).TrimEnd();
        if (body.EndsWith("}")) body += ",";

        string tail = raw.Substring(close + 1);   // whatever followed the bracket

        File.WriteAllText(ITEM_DATA, body + "\n" + BuildEntry() + "\n]" + tail);
        AssetDatabase.Refresh();

        Debug.Log($"[NewItem] Added '{_id}' to {ITEM_DATA}.");
        _lastWritten = _id;

        // The id, and only the id. Authoring a set means five more entries that differ
        // by a word, and clearing the whole form would make the tool slower than the
        // text editor it replaces.
        _existingIds.Add(_id);
        _id = "";
        GUI.FocusControl(null);
    }

    // ── Small helpers ─────────────────────────────────────────────────────────

    private static string[] ActionsFor(int triggerIndex)
    {
        string trigger = Triggers[Mathf.Clamp(triggerIndex, 0, Triggers.Length - 1)];
        return ActionsByTrigger.TryGetValue(trigger, out var actions)
            ? actions
            : new[] { "statBonus" };
    }

    private static int IndexOf(string[] haystack, string needle)
    {
        if (haystack == null) return -1;
        for (int i = 0; i < haystack.Length; i++)
            if (haystack[i] == (needle ?? "")) return i;
        return -1;
    }

    /// <summary>A JSON string literal, with the two characters that would break it escaped.</summary>
    private static string Json(string value)
    {
        value ??= "";
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Trim() + "\"";
    }

    /// <summary>"Tin Helmet" becomes "tin_helmet", the way every id in the file is written.</summary>
    private static string Slug(string value)
    {
        string slug = Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9]+", "_");
        return slug.Trim('_');
    }

    private static void Section(string title)
    {
        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
    }
}
