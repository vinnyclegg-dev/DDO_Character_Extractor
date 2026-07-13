using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VoK.Sdk;
using VoK.Sdk.Common;
using VoK.Sdk.Ddo;
using VoK.Sdk.Ddo.Enums;
using VoK.Sdk.Enums;
using VoK.Sdk.Events;
using VoK.Sdk.Plugins;
using VoK.Sdk.Properties;

namespace VoK.CharacterExtractor;

public sealed class CharacterExtractorPlugin : IDdoPlugin
{
    // ---- IPlugin metadata --------------------------------------------------
    public Guid    PluginId    => new("11111111-2222-3333-4444-555555555555");
    public GameId  Game        => GameId.DDO;
    public string  PluginKey   => "character-extractor";
    public string  Name        => "Character Extractor";
    public string  Description => "Dumps a full character snapshot (with name resolution) to JSON.";
    public string  Author      => "you";
    public Version Version     => new(0, 11, 0, 0);

    public IPluginUI? GetPluginUI() => null;

    // ---- runtime state -----------------------------------------------------
    private IDdoGameDataProvider? _data;
    private IPropertyMaster?      _pm;
    private string                _outDir = "";

    // Cache: weenieId -> resolved name (avoids slamming GetWeenieProperties).
    private readonly Dictionary<uint, string> _nameCache = new();

    // Items the player has examined this session (IID -> snapshot). The engine
    // only streams weenie data when an item is actively examined, so we
    // accumulate them across the session and snapshot from the cache at dump time.
    private readonly Dictionary<ulong, ExaminedItem> _examinedItems = new();

    private sealed class ExaminedItem
    {
        public ulong InstanceId  { get; init; }
        public uint  WeenieId    { get; init; }
        public string? EntityName { get; init; }
        public string? Resolved  { get; init; }
        public List<Dictionary<string, object?>> Properties { get; init; } = new();
        public int? ItemSlotRaw { get; init; }
        public string? DefaultSlot { get; init; }
        public DateTime CapturedUtc { get; init; }
    }

    // BitField32 flags that aren't actual equipment slots — they're inventory
    // container categories that show up alongside the real slot name.
    private static readonly HashSet<string> _slotMetaFlags = new(StringComparer.Ordinal)
    {
        "Equipment", "Backpack", "PetEquipment", "Container", "Bank",
        "Reward", "Shop", "ShopAstralShards", "Overflow", "Cosmetic_Weapon1",
    };

    private static readonly string LogPath = Path.Combine(
        Path.GetTempPath(), "character-extractor.log");

    private static void Log(string msg)
    {
        try
        {
            File.AppendAllText(LogPath,
                $"{DateTime.Now:HH:mm:ss.fff}  {msg}{Environment.NewLine}");
        }
        catch { }
    }

    // ---- IDdoPlugin --------------------------------------------------------
    public void Initialize(IDdoGameDataProvider data, string pluginDataDir)
    {
        try
        {
            Log($"Initialize. pluginDataDir={pluginDataDir ?? "<null>"}");
            _data = data;
            _pm   = data.PropertyMaster;

            _outDir = !string.IsNullOrWhiteSpace(pluginDataDir)
                ? Path.Combine(pluginDataDir, "extracts")
                : Path.Combine(Path.GetTempPath(), "character-extractor", "extracts");
            Directory.CreateDirectory(_outDir);

            var ev = data.EventProvider;

            // Login fires per character — wipe any examine-cache from a
            // previous character so we don't mingle their gear into this one.
            // (No dump is written here.)
            ev.OnLogin.AddHandler(id =>
            {
                Log($"OnLogin id={id} — examine-cache cleared");
                _examinedItems.Clear();
                return Task.CompletedTask;
            });

            // Capture items as the player examines them — the engine only
            // streams weenie data on demand. The handler gets the item's IID
            // and its full property collection in one shot.
            ev.OnExamineObject.AddHandler((iid, props) =>
            {
                try { CaptureExaminedObject(iid, props); }
                catch (Exception ex) { Log($"OnExamineObject capture: {ex.Message}"); }
                return Task.CompletedTask;
            });

            // F12 — manual dump on demand. Press this after examining gear
            // and before logging out (or whenever you want a snapshot).
            ev.KeyDown.AddHandler(args =>
            {
                try
                {
                    if (args is null) return Task.CompletedTask;
                    if (args.DirectInputKey == (uint)DirectInputKeys.DIK_F12)
                    {
                        Log("F12 pressed — writing manual dump");
                        SafeDump("manual");
                    }
                }
                catch (Exception ex) { Log($"KeyDown handler: {ex.Message}"); }
                return Task.CompletedTask;
            });

            // Also write on logout, as a belt-and-braces fallback in case
            // F12 was forgotten.
            ev.OnLogout.AddHandler(() =>
            {
                Log("OnLogout — writing final dump");
                SafeDump("logout");
                return Task.CompletedTask;
            });
        }
        catch (Exception ex) { Log($"Initialize FAILED: {ex}"); }
    }

    public void Terminate() => Log("Terminate");

    // ---- top-level dump ----------------------------------------------------
    private void SafeDump(string reason)
    {
        try { Dump(reason); }
        catch (Exception ex) { Log($"Dump('{reason}') FAILED: {ex}"); }
    }

    private void Dump(string reason)
    {
        if (_data is null) { Log("Dump: _data null"); return; }

        IEntity? player = _data.GetCurrentCharacter();
        if (player is null) { Log("Dump: player null"); return; }

        // Walking the player's PropertyCollection directly is the most reliable
        // way to surface what the engine actually has set on this character —
        // way more useful than asking by enum name.
        var allProps = ToDict(player.PropertyCollection);
        _nameCache.Clear();
        Log($"player has {allProps.Count} properties; weenieId={player.WeenieId}");

        var snapshot = new Dictionary<string, object?>
        {
            ["reason"]       = reason,
            ["timestampUtc"] = DateTime.UtcNow.ToString("o"),
            ["server"]       = SafeCall(() => _data.GetServerName()),
            ["accountHash"]  = "",   // privacy: emit blank. Key kept so the dump schema stays
                                   // compatible with the viewer + DDOBuilder/DDOTracker import.
            ["character"]    = DumpCharacterCore(player),
            ["abilities"]    = SafeCall(() => DumpAbilities(player.PropertyCollection)),
            ["skills"]       = SafeCall(() => DumpByPrefix(allProps, "Skill_", trimPrefix: true)),
            ["feats"]        = SafeCall(() => DumpByPrefix(allProps, "Feat_",  trimPrefix: true,
                                          excludeContains: new[] { "Prerequisite", "MonsterFeats" })),
            ["pastLives"]    = SafeCall(() => DumpPastLives(allProps)),
            ["tomes"]        = SafeCall(() => DumpTomes(allProps)),
            ["enhancements"] = SafeCall(() => DumpTrees(_data.Enhancements,    "enhancement")),
            ["destinies"]    = SafeCall(() => DumpTrees(_data.DestinyTrees,    "destiny")),
            ["reaper"]       = SafeCall(() => DumpTrees(_data.ReaperTrees,     "reaper")),
            ["activeEffects"]= SafeCall(() => DumpActiveEffects()),
            // v0.5: the player has its own Examination_Profile_m_aEffect array
            // which is the engine's unified list of every effect currently
            // applied to the character — gear effects, augment effects,
            // set bonus effects, buffs/auras, everything. Each entry has an
            // iidOriginator pointing back to the IID that caused it, so the
            // viewer can attribute every line back to its source.
            ["playerEffects"]= SafeCall(() => DumpPlayerEffects(player)),
            // v0.5: scan player property bag for set-bonus-named properties
            // (anything containing "SetBonus"/"Set_Bonus" or starting "Set_").
            // Set thresholds and applied tiers should surface here even if the
            // unified effect array above doesn't carry them explicitly.
            ["setBonuses"]   = SafeCall(() => DumpSetBonusProperties(allProps)),
            // v0.5: source-broken-down secondary stats (mirrors DumpAbilities).
            // If the engine populates per-source totals for these, we get the
            // authoritative "what bonus type came from where" breakdown
            // straight from the game — no client-side parsing required.
            ["saves"]        = SafeCall(() => DumpSourcedStat(player.PropertyCollection,
                                          new[] { "Fortitude", "Reflex", "Will" })),
            ["defenses"]     = SafeCall(() => DumpSourcedStat(player.PropertyCollection,
                                          new[] { "ArmorClass", "PhysicalSheltering",
                                                  "MagicalSheltering", "Fortification",
                                                  "Dodge", "Concealment", "HealingAmplification",
                                                  "NegativeHealingAmplification",
                                                  "RepairAmplification" })),
            ["combat"]       = SafeCall(() => DumpSourcedStat(player.PropertyCollection,
                                          new[] { "Doublestrike", "Doubleshot",
                                                  "MeleePower", "RangedPower",
                                                  "UniversalSpellPower",
                                                  "AttackBonus", "DamageBonus",
                                                  "BypassFortification",
                                                  "CriticalHitChance",
                                                  "CriticalHitConfirmation",
                                                  "CriticalDamageBonus",
                                                  "ImbueDiceChance" })),
            // v0.5: elemental/energy resistance breakdowns. Same pattern.
            ["resistances"]  = SafeCall(() => DumpSourcedStat(player.PropertyCollection,
                                          new[] { "ColdResistance", "FireResistance",
                                                  "AcidResistance", "ElectricResistance",
                                                  "SonicResistance", "PoisonResistance",
                                                  "NegativeResistance", "ForceResistance",
                                                  "LightResistance", "ChaosResistance",
                                                  "EvilResistance", "GoodResistance",
                                                  "LawResistance" })),
            // v0.5: misc utility stats often modified by gear.
            ["misc"]         = SafeCall(() => DumpSourcedStat(player.PropertyCollection,
                                          new[] { "MovementSpeed", "ActionPoints",
                                                  "MaxCarriedWeight",
                                                  "FalseLife", "TemporarySpellPoints",
                                                  "Threat", "Stealth", "SpellResistance" })),
            ["equipped"]     = SafeCall(() => DumpEquippedFromProps(allProps, player)),
            // v0.6: raw examine cache (every item the user has ever examined this
            // session) indexed by weenieId for fast cross-reference. equipped[]
            // already includes this but with slot-matching logic that obscures
            // non-gear examined items (augments, inventory items, vendor previews).
            // If the user examines an augment ITEM separately, its property bag
            // — including its own Examination_Profile_m_aEffect with the slotted-
            // effect strings — shows up here keyed by the augment's weenieId.
            ["examineCache"] = SafeCall(() => DumpExamineCache()),
            // v0.6: every IID-shaped UInt64 in the player's property bag, with
            // GetEntity resolved info (weenieId, name, slot). Discovers entities
            // the player references but hasn't examined (inventory items, swap
            // gear in bags, vault contents, etc.). Best-effort: GetEntity may
            // return null for IIDs the engine hasn't streamed.
            ["playerIidReferences"] = SafeCall(() => DumpPlayerIidReferences(allProps)),
            // v0.6: reflection-based diagnostic of the runtime type backing
            // IDdoGameDataProvider. Lists every public method and property so
            // we (and you) can discover SDK capabilities we haven't tapped —
            // anything named GetSetBonus, GetSpellBook, GetInventory, etc.
            ["providerCapabilities"] = SafeCall(() => DumpProviderCapabilities()),
            // v0.7: SDK methods discovered via providerCapabilities. Capture
            // every entity the engine has streamed (with full property bag for
            // player-owned items), plus server/subscription/quest/hotbar/party
            // state.
            ["allEntities"]          = SafeCall(() => DumpAllEntities(player)),
            ["allEntityIds"]         = SafeCall(() => DumpAllEntityIds()),
            ["serverProperties"]     = SafeCall(() => DumpServerProperties()),
            ["subscriptionProperties"] = SafeCall(() => DumpSubscriptionProperties()),
            ["questState"]           = SafeCall(() => DumpQuestState()),
            ["hotbarMap"]            = SafeCall(() => DumpHotbarMap()),
            ["inventoryOptions"]     = SafeCall(() => DumpInventoryOptions()),
            ["partyMembers"]         = SafeCall(() => DumpPartyMembers()),
            ["allProperties"]= SafeCall(() => DumpRawProps(player.PropertyCollection)),
            ["buildData"]    = SafeCall(() => DumpBuildData()),
            // v0.10: explicit DID -> name lookup for class & feat DIDs found
            // in level history. The engine resolves these via ResolveName(did)
            // but the viewer's DID_NAMES index (built from items/effects/trees)
            // misses them, so we emit a pre-resolved map.
            ["didNameMap"]   = SafeCall(() => DumpLevelHistoryDidNames(allProps)),
        };

        var server   = (snapshot["server"] as string ?? "unknown").Replace(' ', '_');
        var safeName = string.Concat((player.Name ?? "char").Split(Path.GetInvalidFileNameChars()));
        var stamp    = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var path     = Path.Combine(_outDir, $"{server}_{safeName}_{stamp}_{reason}.json");

        File.WriteAllText(path,
            JsonConvert.SerializeObject(snapshot, Formatting.Indented, JsonSettings));
        Log($"wrote: {path}");
    }

    // ---- name resolution ---------------------------------------------------

    /// <summary>Resolve a weenieId / DataID to its in-game display name. Tries
    /// every plausible string-info property and falls back to logging the
    /// weenie's actual property keys so we can see what name field exists.</summary>
    private string? ResolveName(uint weenieId)
    {
        if (weenieId == 0) return null;
        if (_nameCache.TryGetValue(weenieId, out var cached)) return cached;
        try
        {
            var props = _data!.GetWeenieProperties(weenieId);
            if (props is null)
            {
                Log($"ResolveName({weenieId}): GetWeenieProperties returned null");
                return _nameCache[weenieId] = $"did_{weenieId}";
            }

            // 1. Walk the weenie's StringInfo properties — pick the first that
            //    resolves to a non-key text. We try multiple resolution paths
            //    because the SDK's .Text getter returns the raw key when the
            //    string table isn't loaded; GetStringEntry(_pm).Value goes
            //    through the property master and gives the actual translation.
            try
            {
                if (props.Properties is not null)
                {
                    foreach (var kvp in props.Properties)
                    {
                        var p = kvp.Value;
                        if (p is null) continue;
                        var name = (p.PropertyName ?? "").ToString() ?? "";
                        if (!name.Contains("Name", StringComparison.Ordinal)) continue;

                        // Best path: GetStringEntry through the propertyMaster.
                        if (p is IStringInfoProperty sip)
                        {
                            if (sip.IsLiteral && IsRealText(sip.Text))
                                return _nameCache[weenieId] = sip.Text!;

                            if (_pm is not null)
                            {
                                try
                                {
                                    var entry = sip.GetStringEntry(_pm);
                                    var v = entry?.Value;
                                    if (IsRealText(v))
                                        return _nameCache[weenieId] = v!;
                                }
                                catch { }
                                if (sip.Key.HasValue && sip.Table.HasValue)
                                {
                                    try
                                    {
                                        var v = _pm.GetString(sip.Key.Value, sip.Table.Value);
                                        if (IsRealText(v))
                                            return _nameCache[weenieId] = v!;
                                    }
                                    catch { }
                                }
                                try
                                {
                                    var v = sip.GetText(_pm, weenieId, props);
                                    if (IsRealText(v))
                                        return _nameCache[weenieId] = v!;
                                }
                                catch { }
                            }
                        }

                        if (p is IStringProperty strp && IsRealText(strp.StringValue))
                            return _nameCache[weenieId] = strp.StringValue!;
                    }
                }
            }
            catch (Exception ex) { Log($"ResolveName({weenieId}) walk: {ex.Message}"); }

            // 2. Diagnostic — log the first few property names so we can see
            //    what's actually on this weenie next time.
            try
            {
                var keys = props.Properties?
                    .Take(8)
                    .Select(k => $"{k.Value.PropertyName} [{k.Value.Type}]")
                    .ToList();
                Log($"ResolveName({weenieId}) no name; first props: {string.Join(", ", keys ?? new())}");
            }
            catch { }

            return _nameCache[weenieId] = $"did_{weenieId}";
        }
        catch (Exception ex)
        {
            Log($"ResolveName({weenieId}): {ex.Message}");
            return _nameCache[weenieId] = $"did_{weenieId}";
        }
    }

    /// <summary>v0.8: fetch a DID's effect text. Augments/filigree don't carry
    /// effects on their template's Examination_Profile_m_aEffect — but they DO
    /// carry the bonus text as plain English on Item_Description (e.g. "Drag
    /// this augment into a slot to upgrade an item with a +5 Insight Bonus to
    /// Charisma"). We try in order:
    ///   1. weenie's Examination_Profile_m_aEffect (existing path, usually
    ///      empty for augments)
    ///   2. weenie's Item_Description — wrapped into a synthetic effect entry
    ///      so the viewer's existing regex-based parser picks it up unchanged
    ///   3. examine cache by weenieId — for items the user examined
    ///   4. live GetEntities() scan by weenieId — captures unslotted augments
    ///      currently in the player's inventory whose properties the engine
    ///      has streamed (e.g. Item_Description with the bonus text)
    /// Returns a list of effect-entry-shaped dicts (compatible with the host
    /// item's Examination_Profile_m_aEffect schema) or null.</summary>
    private object? GetItemEffectsByDid(uint did)
    {
        if (_data is null || did == 0) return null;
        // 1. Weenie's Examination_Profile_m_aEffect
        try
        {
            var weenie = _data.GetWeenieProperties(did);
            if (weenie?.Properties is not null)
            {
                foreach (var kvp in weenie.Properties)
                {
                    var p = kvp.Value;
                    if (p?.PropertyName?.ToString() == "Examination_Profile_m_aEffect")
                    {
                        var v = UnwrapProperty(p);
                        if (v is List<Dictionary<string, object?>> list && list.Count > 0) return list;
                    }
                }
                // 2. Weenie's Item_Description — synthesize an effect-entry struct
                var descText = ExtractItemDescription(weenie.Properties);
                if (!string.IsNullOrEmpty(descText))
                    return new List<Dictionary<string, object?>> { SyntheticEffectEntry(descText) };
            }
        }
        catch (Exception ex) { Log($"GetItemEffectsByDid({did}) weenie: {ex.Message}"); }
        // 3. Examine cache by weenieId — either real effect array or Item_Description
        try
        {
            foreach (var (iid, snap) in _examinedItems)
            {
                if (snap.WeenieId != did) continue;
                foreach (var p in snap.Properties)
                {
                    var pname = (string?)p.GetValueOrDefault("name");
                    if (pname == "Examination_Profile_m_aEffect")
                    {
                        var v = p.GetValueOrDefault("value");
                        if (v is System.Collections.IList l && l.Count > 0) return v;
                    }
                }
                // Also try Item_Description from the cached snapshot
                var descText = ExtractItemDescriptionFromDump(snap.Properties);
                if (!string.IsNullOrEmpty(descText))
                    return new List<Dictionary<string, object?>> { SyntheticEffectEntry(descText) };
            }
        }
        catch (Exception ex) { Log($"GetItemEffectsByDid({did}) cache: {ex.Message}"); }
        // 4. Live GetEntities() scan — same weenieId, pull Item_Description if present
        try
        {
            var entities = _data?.GetEntities();
            if (entities is not null)
            {
                foreach (var ent in entities)
                {
                    if (ent.WeenieId != did) continue;
                    var pc = ent.PropertyCollection;
                    if (pc?.Properties is null) continue;
                    foreach (var kv in pc.Properties)
                    {
                        var p = kv.Value;
                        if (p?.PropertyName?.ToString() == "Examination_Profile_m_aEffect")
                        {
                            var v = UnwrapProperty(p);
                            if (v is List<Dictionary<string, object?>> list && list.Count > 0) return list;
                        }
                    }
                    var descText = ExtractItemDescription(pc.Properties);
                    if (!string.IsNullOrEmpty(descText))
                        return new List<Dictionary<string, object?>> { SyntheticEffectEntry(descText) };
                    break; // first matching entity is enough
                }
            }
        }
        catch (Exception ex) { Log($"GetItemEffectsByDid({did}) entities: {ex.Message}"); }
        return null;
    }

    /// <summary>v0.8: read Item_Description from a live property collection.</summary>
    private static string? ExtractItemDescription(IReadOnlyDictionary<uint, IProperty> props)
    {
        try
        {
            foreach (var kvp in props)
            {
                var p = kvp.Value;
                if (p?.PropertyName?.ToString() != "Item_Description") continue;
                if (p is IStringInfoProperty sip)
                {
                    if (IsRealText(sip.Text)) return sip.Text;
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>v0.8: read Item_Description from a dumped properties list (as
    /// stored in _examinedItems.Properties).</summary>
    private static string? ExtractItemDescriptionFromDump(List<Dictionary<string, object?>> props)
    {
        try
        {
            foreach (var p in props)
            {
                if ((string?)p.GetValueOrDefault("name") != "Item_Description") continue;
                var v = p.GetValueOrDefault("value");
                if (v is string s && IsRealText(s)) return s;
                if (v is Dictionary<string, object?> d && d.GetValueOrDefault("text") is string t && IsRealText(t)) return t;
            }
        }
        catch { }
        return null;
    }

    /// <summary>v0.8: wrap a description string into a struct that matches the
    /// Examination_Profile_m_EffectEntry schema, so the viewer's regex-based
    /// parser (which scans EffectDynamicDesc fields) handles it unchanged.</summary>
    private static Dictionary<string, object?> SyntheticEffectEntry(string desc)
        => new()
        {
            ["id"]   = 0,
            ["name"] = "Examination_Profile_m_EffectEntry",
            ["type"] = "Struct",
            ["value"] = new List<Dictionary<string, object?>>
            {
                new() { ["name"] = "Examination_Profile_m_EffectDynamicDesc", ["type"] = "StringInfo", ["value"] = desc },
                new() { ["name"] = "__synthetic", ["type"] = "Bool", ["value"] = true },
            },
        };

    /// <summary>Dump the full examine cache indexed by weenieId for fast lookup.
    /// This is the raw _examinedItems state — every item the user has ever
    /// examined this session, no slot-matching filter. Includes augments,
    /// inventory items, vendor previews, anything that fired OnExamineObject.
    /// Keyed by weenieId.ToString() so JSON consumers can index by DID. If two
    /// IIDs share a weenieId (same item template, different instances), the
    /// first cached wins.</summary>
    private object DumpExamineCache()
    {
        var byWeenie = new Dictionary<string, object?>();
        foreach (var (iid, snap) in _examinedItems)
        {
            if (snap.WeenieId == 0) continue;
            var key = snap.WeenieId.ToString();
            if (byWeenie.ContainsKey(key)) continue;
            byWeenie[key] = new Dictionary<string, object?>
            {
                ["instanceId"]  = iid,
                ["weenieId"]    = snap.WeenieId,
                ["entityName"]  = snap.EntityName,
                ["resolved"]    = snap.Resolved,
                ["defaultSlot"] = snap.DefaultSlot,
                ["itemSlotRaw"] = snap.ItemSlotRaw,
                ["capturedUtc"] = snap.CapturedUtc.ToString("o"),
                ["properties"]  = snap.Properties,
            };
        }
        return byWeenie;
    }

    /// <summary>Walk the player's property bag for any UInt64 value that looks
    /// like an IID (greater than 100,000,000 — IIDs in DDO are typically huge
    /// 64-bit numbers, while regular ints and timestamps fall below this).
    /// For each candidate, try _data.GetEntity(iid) and dump basic info if the
    /// engine has streamed that entity. Doesn't pull the full property bag
    /// (potentially expensive — use OnExamineObject for that). Output: dict
    /// keyed by iid.ToString() mapping to {weenieId, name, slot, source_props}
    /// so the viewer can build a complete IID-to-entity map.</summary>
    private object DumpPlayerIidReferences(Dictionary<string, IProperty> allProps)
    {
        const ulong IidThreshold = 100_000_000UL;
        var seenIids = new HashSet<ulong>();
        var sourceMap = new Dictionary<ulong, List<string>>();
        // First pass: collect candidate IIDs and which property names referenced them.
        foreach (var (name, p) in allProps)
        {
            ulong iid = 0;
            try
            {
                if (p is IUInt64Property up && up.UInt64Value.HasValue)
                    iid = up.UInt64Value.Value;
                else if (p is IInt64Property lp && lp.Int64Value.HasValue && lp.Int64Value.Value > 0)
                    iid = (ulong)lp.Int64Value.Value;
            }
            catch { }
            if (iid <= IidThreshold) continue;
            seenIids.Add(iid);
            if (!sourceMap.TryGetValue(iid, out var list)) sourceMap[iid] = list = new();
            list.Add(name);
        }
        // Second pass: resolve each candidate via GetEntity.
        var result = new Dictionary<string, object?>();
        foreach (var iid in seenIids)
        {
            try
            {
                var entry = new Dictionary<string, object?> { ["sources"] = sourceMap[iid] };
                var ent = _data?.GetEntity(iid);
                if (ent is not null)
                {
                    entry["weenieId"] = ent.WeenieId;
                    entry["name"]     = ent.Name;
                    if (ent.WeenieId.HasValue)
                        entry["resolvedName"] = ResolveName(ent.WeenieId.Value);
                    // If we've cached an examined snapshot for this IID, mark it.
                    entry["examined"] = _examinedItems.ContainsKey(iid);
                }
                else
                {
                    entry["entityAvailable"] = false;
                }
                result[iid.ToString()] = entry;
            }
            catch (Exception ex) { Log($"DumpPlayerIidReferences({iid}): {ex.Message}"); }
        }
        return result;
    }

    /// <summary>v0.7: dump every entity the engine has currently streamed via
    /// _data.GetEntities(). For each entity, dump basic info (iid, weenieId,
    /// resolved name). If the entity is owned by the player (has an
    /// Inventory_OwnerIID matching the player's instanceId, or has any
    /// Inventory_* property at all), also dump its full property bag — this
    /// catches every item in the player's bags, vault, etc. that the engine
    /// has loaded, without requiring the user to examine each one. Capped at
    /// 5000 entries to prevent runaway JSON in busy zones.</summary>
    private object DumpAllEntities(IEntity player)
    {
        var byIid = new Dictionary<string, object?>();
        try
        {
            var entities = _data?.GetEntities();
            if (entities is null) return byIid;
            var playerIid = player.InstanceId;
            int fullDumped = 0, basicDumped = 0, total = 0;
            foreach (var ent in entities)
            {
                if (++total > 5000) break;
                try
                {
                    var iid = ent.InstanceId;
                    if (iid == 0) continue;

                    bool ownedByPlayer = false;
                    bool hasInventoryProps = false;
                    var pc = ent.PropertyCollection;
                    if (pc?.Properties is not null)
                    {
                        foreach (var kv in pc.Properties)
                        {
                            var p = kv.Value;
                            var pname = p?.PropertyName?.ToString();
                            if (pname is null) continue;
                            if (pname.StartsWith("Inventory_", StringComparison.Ordinal)) hasInventoryProps = true;
                            if (pname == "Inventory_OwnerIID" || pname == "Owner_IID" ||
                                pname == "Inventory_Owner_IID" || pname == "Container_IID")
                            {
                                if (p is IUInt64Property up && up.UInt64Value == playerIid) ownedByPlayer = true;
                                else if (p is IInt64Property lp && lp.Int64Value.HasValue &&
                                         (ulong)lp.Int64Value.Value == playerIid) ownedByPlayer = true;
                            }
                        }
                    }

                    var entry = new Dictionary<string, object?>
                    {
                        ["instanceId"]      = iid,
                        ["weenieId"]        = ent.WeenieId,
                        ["name"]            = ent.Name,
                        ["resolvedName"]    = ent.WeenieId.HasValue ? ResolveName(ent.WeenieId.Value) : null,
                        ["ownedByPlayer"]   = ownedByPlayer,
                        ["hasInventoryProps"] = hasInventoryProps,
                        ["examined"]        = _examinedItems.ContainsKey(iid),
                    };

                    // Dump full property bag if owned by player or has inventory props
                    // (catches items in bags, vault, etc.). Skip already-examined IIDs
                    // since equipped[] / examineCache already has them.
                    if ((ownedByPlayer || hasInventoryProps) && !_examinedItems.ContainsKey(iid) &&
                        pc?.Properties is not null)
                    {
                        entry["properties"] = DumpPropertyMap(pc.Properties);
                        fullDumped++;
                    }
                    else { basicDumped++; }

                    byIid[iid.ToString()] = entry;
                }
                catch (Exception ex) { Log($"DumpAllEntities entry: {ex.Message}"); }
            }
            Log($"DumpAllEntities: {fullDumped} full + {basicDumped} basic (total seen: {total})");
        }
        catch (Exception ex) { Log($"DumpAllEntities: {ex.Message}"); }
        return byIid;
    }

    /// <summary>v0.7: dump all entity IIDs the engine knows about (may exceed
    /// what's in GetEntities() if some are referenced but not streamed).</summary>
    private object DumpAllEntityIds()
    {
        try
        {
            var ids = _data?.GetAllEntityIds();
            if (ids is null) return new List<ulong>();
            return ids.Select(i => i).ToList();
        }
        catch (Exception ex) { Log($"DumpAllEntityIds: {ex.Message}"); return new List<ulong>(); }
    }

    /// <summary>v0.7: server-level property collection (zone state, etc.).</summary>
    private object DumpServerProperties()
    {
        try
        {
            var props = _data?.GetServerProperties();
            if (props is null) return new List<Dictionary<string, object?>>();
            return DumpPropertyList(props);
        }
        catch (Exception ex) { Log($"DumpServerProperties: {ex.Message}"); return new List<Dictionary<string, object?>>(); }
    }

    /// <summary>v0.7: account/subscription-level property collection.</summary>
    private object DumpSubscriptionProperties()
    {
        try
        {
            var props = _data?.GetSubscriptionProperties();
            if (props is null) return new List<Dictionary<string, object?>>();
            return DumpPropertyList(props);
        }
        catch (Exception ex) { Log($"DumpSubscriptionProperties: {ex.Message}"); return new List<Dictionary<string, object?>>(); }
    }

    /// <summary>v0.7: current quest state via the discovered SDK methods.</summary>
    private object DumpQuestState()
    {
        var result = new Dictionary<string, object?>();
        try { result["currentQuestDid"] = _data?.GetCurrentQuestDid(); } catch (Exception ex) { Log($"currentQuestDid: {ex.Message}"); }
        try { result["instanceQuestDid"] = _data?.GetInstanceQuestDid(); } catch (Exception ex) { Log($"instanceQuestDid: {ex.Message}"); }
        try { result["currentObjectiveDid"] = _data?.GetCurrentQuestObjectiveDid(); } catch (Exception ex) { Log($"currentObjectiveDid: {ex.Message}"); }
        try { result["inTown"] = _data?.InTown(); } catch (Exception ex) { Log($"inTown: {ex.Message}"); }
        try { result["serverTimestamp"] = _data?.GetServerTimestamp(); } catch (Exception ex) { Log($"serverTimestamp: {ex.Message}"); }
        // Resolve quest DID to name if present
        try
        {
            if (result.TryGetValue("currentQuestDid", out var qd) && qd is uint q && q != 0)
                result["currentQuestName"] = ResolveName(q);
        }
        catch { }
        return result;
    }

    /// <summary>v0.7: hotbar slot map (player's quickbar configuration).</summary>
    private object DumpHotbarMap()
    {
        try
        {
            var slots = _data?.GetHotbarMap();
            if (slots is null) return new List<object?>();
            var list = new List<Dictionary<string, object?>>();
            for (int i = 0; i < slots.Length; i++)
            {
                var s = slots[i];
                if (s is null) continue;
                // Best-effort reflection over the hotbar slot to grab common fields.
                var entry = new Dictionary<string, object?> { ["index"] = i };
                try
                {
                    var t = s.GetType();
                    foreach (var p in t.GetProperties())
                    {
                        if (!p.CanRead) continue;
                        try { entry[p.Name] = SimpleValue(p.GetValue(s)); } catch { }
                    }
                }
                catch { }
                list.Add(entry);
            }
            return list;
        }
        catch (Exception ex) { Log($"DumpHotbarMap: {ex.Message}"); return new List<object?>(); }
    }

    /// <summary>v0.7: inventory options (sort settings, filter state, etc.).</summary>
    private object DumpInventoryOptions()
    {
        try
        {
            var opts = _data?.GetInventoryOptions();
            if (opts is null) return new Dictionary<string, object?>();
            var t = opts.GetType();
            var result = new Dictionary<string, object?> { ["runtimeType"] = t.FullName };
            foreach (var p in t.GetProperties())
            {
                if (!p.CanRead) continue;
                try { result[p.Name] = SimpleValue(p.GetValue(opts)); } catch { }
            }
            return result;
        }
        catch (Exception ex) { Log($"DumpInventoryOptions: {ex.Message}"); return new Dictionary<string, object?> { ["error"] = ex.Message }; }
    }

    /// <summary>v0.7: party member roster.</summary>
    private object DumpPartyMembers()
    {
        try
        {
            var party = _data?.GetPartyMembers();
            if (party is null) return new List<object?>();
            var list = new List<Dictionary<string, object?>>();
            foreach (var m in party)
            {
                if (m is null) { list.Add(new Dictionary<string, object?>()); continue; }
                var t = m.GetType();
                var entry = new Dictionary<string, object?>();
                foreach (var p in t.GetProperties())
                {
                    if (!p.CanRead) continue;
                    try { entry[p.Name] = SimpleValue(p.GetValue(m)); } catch { }
                }
                list.Add(entry);
            }
            return list;
        }
        catch (Exception ex) { Log($"DumpPartyMembers: {ex.Message}"); return new List<object?>(); }
    }

    /// <summary>v0.10: walk FeatRespec_FeatChoicesArray, collect every class
    /// and feat DID encountered across the character's level history, resolve
    /// each via ResolveName (engine weenie lookup), and emit the {did → name}
    /// map. The viewer's DID_NAMES index doesn't otherwise cover these DIDs
    /// because they're not items, augments, or trees.</summary>
    private Dictionary<string, object?> DumpLevelHistoryDidNames(
        Dictionary<string, IProperty> allProps)
    {
        var result = new Dictionary<string, object?>();
        if (!allProps.TryGetValue("FeatRespec_FeatChoicesArray", out var fra)) return result;
        try
        {
            foreach (var entry in AsEnumerable(UnwrapProperty(fra)))
            {
                if (entry is not Dictionary<string, object?> dict) continue;
                var sub = ExtractStructFields(dict);
                // Class DID
                if (AsInt(sub.GetValueOrDefault("FeatRespec_Class")) is int cls && cls > 0)
                {
                    var key = ((uint)cls).ToString();
                    if (!result.ContainsKey(key))
                    {
                        var name = ResolveName((uint)cls);
                        if (!string.IsNullOrEmpty(name) && name != $"did_{cls}") result[key] = name;
                    }
                }
                // Feat DIDs (list)
                if (sub.GetValueOrDefault("FeatRespec_FeatList") is IEnumerable<object?> feats)
                {
                    foreach (var f in feats)
                    {
                        int? did = null;
                        if (f is Dictionary<string, object?> fd && fd.GetValueOrDefault("value") is int fi) did = fi;
                        else did = AsInt(f);
                        if (did is int v && v > 0)
                        {
                            var key = ((uint)v).ToString();
                            if (!result.ContainsKey(key))
                            {
                                var name = ResolveName((uint)v);
                                if (!string.IsNullOrEmpty(name) && name != $"did_{v}") result[key] = name;
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex) { Log($"DumpLevelHistoryDidNames: {ex.Message}"); }
        return result;
    }

    /// <summary>Reflection diagnostic — list every public method and readable
    /// property on the runtime type backing IDdoGameDataProvider. Useful for
    /// discovering SDK capabilities we haven't tapped (e.g. a GetSetBonus,
    /// GetSpellBook, GetInventory method we don't know exists). Runs once per
    /// dump; cost is negligible.</summary>
    private object DumpProviderCapabilities()
    {
        try
        {
            var t = _data?.GetType();
            if (t is null) return new Dictionary<string, object?> { ["error"] = "no _data" };
            var methods = t.GetMethods()
                .Where(m => m.IsPublic && !m.IsSpecialName && m.DeclaringType != typeof(object))
                .Select(m => new Dictionary<string, object?>
                {
                    ["name"]       = m.Name,
                    ["returnType"] = m.ReturnType.FullName,
                    ["params"]     = m.GetParameters()
                                       .Select(pa => $"{pa.ParameterType.Name} {pa.Name}")
                                       .ToList(),
                })
                .OrderBy(d => d["name"]?.ToString())
                .ToList();
            var properties = t.GetProperties()
                .Where(p => p.CanRead)
                .Select(p => new Dictionary<string, object?>
                {
                    ["name"] = p.Name,
                    ["type"] = p.PropertyType.FullName,
                })
                .OrderBy(d => d["name"]?.ToString())
                .ToList();
            return new Dictionary<string, object?>
            {
                ["runtimeType"] = t.FullName,
                ["interfaces"]  = t.GetInterfaces().Select(i => i.FullName).ToList(),
                ["methods"]     = methods,
                ["properties"]  = properties,
            };
        }
        catch (Exception ex) { Log($"DumpProviderCapabilities: {ex.Message}"); return new Dictionary<string, object?> { ["error"] = ex.Message }; }
    }

    /// <summary>Dump the player's own Examination_Profile_m_aEffect array.
    /// In the engine, every effect currently applied to the character (from
    /// gear, augments, filigree, set bonuses, buffs) lives here as a struct
    /// with iidOriginator pointing at the source IID. The plugin's per-item
    /// dump already gives effect text by item; this gives the unified player
    /// view including effects that don't have a host item (set bonuses,
    /// stance procs, party buffs).</summary>
    private object? DumpPlayerEffects(IEntity? player)
    {
        try
        {
            var pc = player?.PropertyCollection;
            if (pc?.Properties is null) return null;
            foreach (var kvp in pc.Properties)
            {
                var p = kvp.Value;
                if (p?.PropertyName?.ToString() == "Examination_Profile_m_aEffect")
                    return UnwrapProperty(p);
            }
        }
        catch (Exception ex) { Log($"DumpPlayerEffects: {ex.Message}"); }
        return null;
    }

    /// <summary>Scan the player's full property bag for anything that smells
    /// like set-bonus state: property names containing "SetBonus" or
    /// "Set_Bonus", or starting with "Set_". Dumps each match with its decoded
    /// value (enum string for enum properties, raw for primitives). The viewer
    /// can then resolve set names against active piece counts to reconstruct
    /// which thresholds are firing.</summary>
    private static List<Dictionary<string, object?>> DumpSetBonusProperties(
        Dictionary<string, IProperty> allProps)
    {
        var result = new List<Dictionary<string, object?>>();
        foreach (var (name, p) in allProps)
        {
            bool match =
                name.IndexOf("SetBonus", StringComparison.Ordinal) >= 0 ||
                name.IndexOf("Set_Bonus", StringComparison.Ordinal) >= 0 ||
                name.StartsWith("Set_", StringComparison.Ordinal) ||
                name.IndexOf("SetTier", StringComparison.Ordinal) >= 0;
            if (!match) continue;
            try
            {
                result.Add(new Dictionary<string, object?>
                {
                    ["name"]  = name,
                    ["type"]  = p.Type.ToString(),
                    ["value"] = UnwrapProperty(p),
                });
            }
            catch { }
        }
        return result;
    }

    /// <summary>Generic source-broken-down stat dump. Mirrors DumpAbilities for
    /// any list of stat root names. Tries every known source suffix; entries
    /// the engine doesn't populate are silently skipped. The set of suffixes
    /// covers the canonical DDO bonus types — Item, Enhancement, Insight,
    /// Quality, Profane, Sacred, Artifact, Exceptional, Stance, Set, etc. If
    /// the engine carries any of these as separate properties, we'll see
    /// engine-authoritative per-source totals here.</summary>
    private static object DumpSourcedStat(IPropertyCollection pc, string[] stats)
    {
        var sources = new[]
        {
            "", "_Base", "_Total", "_Bonus",
            "_Item", "_Enhancement", "_Feat", "_Tome", "_Race", "_Class",
            "_Inherent", "_Insight", "_Insightful", "_Quality", "_Exceptional",
            "_Profane", "_Sacred", "_Artifact", "_Legendary", "_Competence",
            "_Morale", "_Resistance", "_Alchemical", "_Equipment", "_Untyped",
            "_Stance", "_Set", "_Festive", "_Filigree"
        };
        var result = new Dictionary<string, Dictionary<string, object?>>();
        foreach (var stat in stats)
        {
            var bag = new Dictionary<string, object?>();
            foreach (var src in sources)
            {
                if (Enum.TryParse<DdoProperty>(stat + src, out var prop) &&
                    TryRead(pc, (uint)prop, out var v))
                {
                    var key = string.IsNullOrEmpty(src) ? "total" : src.TrimStart('_').ToLowerInvariant();
                    bag[key] = v;
                }
            }
            if (bag.Count > 0) result[stat] = bag;
        }
        return result;
    }

    // ---- section helpers ---------------------------------------------------

    private static object DumpCharacterCore(IEntity p) => new
    {
        instanceId = p.InstanceId,
        weenieId   = p.WeenieId,
        ownerId    = p.OwnerId,
        name       = p.Name,
        hp = new { current = p.HitPoints_Current,   max = p.HitPoints_Max,   temp = p.HitPoints_Temp },
        sp = new { current = p.SpellPoints_Current, max = p.SpellPoints_Max, temp = p.SpellPoints_Temp },
        ki = new { current = p.Ki_Current,          max = p.Ki_Max },
        lifeState = p.LifeState,
    };

    private static object DumpAbilities(IPropertyCollection pc)
    {
        var stats = new[] { "Strength", "Dexterity", "Constitution",
                            "Intelligence", "Wisdom", "Charisma" };
        var sources = new[] { "", "_Base", "_Advancement", "_Feat", "_Damage",
                              "_Item", "_Enhancement", "_Tome", "_Race",
                              "_Inherent", "_Insight" };
        var result = new Dictionary<string, Dictionary<string, object?>>();
        foreach (var stat in stats)
        {
            var bag = new Dictionary<string, object?>();
            foreach (var src in sources)
            {
                if (Enum.TryParse<DdoProperty>(stat + src, out var prop) &&
                    TryRead(pc, (uint)prop, out var v))
                {
                    bag[string.IsNullOrEmpty(src) ? "total" : src.TrimStart('_').ToLowerInvariant()] = v;
                }
            }
            result[stat] = bag;
        }
        return result;
    }

    /// <summary>Walk every property the character actually has and pull the
    /// ones whose name starts with the given prefix.</summary>
    private static Dictionary<string, object?> DumpByPrefix(
        Dictionary<string, IProperty> props, string prefix,
        bool trimPrefix = false, string[]? excludeContains = null)
    {
        var result = new Dictionary<string, object?>();
        foreach (var (name, p) in props)
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (excludeContains != null && excludeContains.Any(x => name.Contains(x))) continue;
            var key = trimPrefix ? name.Substring(prefix.Length) : name;
            try { result[key] = UnwrapProperty(p); } catch { }
        }
        return result;
    }

    private static Dictionary<string, object?> DumpPastLives(Dictionary<string, IProperty> props)
    {
        var result = new Dictionary<string, object?>();
        foreach (var (name, p) in props)
        {
            if (name.StartsWith("Feat_Reincantaion_PastLife_") ||
                name.StartsWith("Benefit_PastLife_")          ||
                name.StartsWith("EpicPastLife_")              ||
                name.StartsWith("IconicPastLife_"))
            {
                try { result[name] = UnwrapProperty(p); } catch { }
            }
        }
        return result;
    }

    private Dictionary<string, object?> DumpTomes(Dictionary<string, IProperty> props)
    {
        var result = new Dictionary<string, object?>();
        foreach (var (name, p) in props)
        {
            if (!name.Contains("Tome", StringComparison.Ordinal)) continue;
            object? val;
            try { val = UnwrapProperty(p); } catch { continue; }

            // If this looks like a TomeDID, resolve it to a name.
            uint? did = val switch
            {
                uint  u            => u,
                int   i when i > 0 => (uint)i,
                _                  => null,
            };
            if (did is uint d && d > 1_000_000 &&
                name.EndsWith("DID", StringComparison.OrdinalIgnoreCase))
                result[name] = new { did = d, name = ResolveName(d), raw = val };
            else
                result[name] = val;
        }
        return result;
    }

    private object DumpTrees(IEnumerable<IEnhancementTree>? trees, string kind)
    {
        var result = new List<Dictionary<string, object?>>();
        if (trees is null) return result;
        foreach (var tree in trees)
        {
            result.Add(new Dictionary<string, object?>
            {
                ["kind"]         = kind,
                ["name"]         = tree.Name,
                ["did"]          = tree.Did,
                ["actionPoints"] = tree.ActionPointSpend,
                ["selected"]     = SafeCall(() => DumpSelectedEnhancements(tree)),
                ["properties"]   = SafeCall(() => DumpPropertyMap(tree.Properties?.Properties)),
            });
        }
        return result;
    }

    /// <summary>Dump the selected-enhancements list. Each element is typically
    /// a primitive (uint DID) or an SDK property wrapper — emit it directly and
    /// try resolving as a DID.
    /// v0.9: now also pulls the enhancement's Item_Description / effects via
    /// GetItemEffectsByDid(did) so tree-derived bonuses (e.g. Tabaxi's
    /// "Dexterity" enhancement granting +1 Dex) are extractable downstream
    /// using the same parser the gear path uses.</summary>
    private object DumpSelectedEnhancements(IEnhancementTree tree)
    {
        var entries = new List<object?>();
        var sel = tree.SelectedEnhancements;
        if (sel is null) return entries;
        foreach (var e in (IEnumerable)sel)
        {
            if (e is null) { entries.Add(null); continue; }

            // If the element is a simple value (or an SDK property wrapper),
            // SimpleValue handles it. We then try to resolve as a DID.
            object? unwrapped = SimpleValue(e);

            uint? did = unwrapped switch
            {
                uint  u            => u,
                int   i when i > 0 => (uint)i,
                long  l when l > 0 => (uint)l,
                ulong ul           => (uint)ul,
                _                  => null,
            };

            if (did is uint d && d > 1_000_000)
            {
                entries.Add(new Dictionary<string, object?>
                {
                    ["did"]     = d,
                    ["name"]    = ResolveName(d) ?? $"did_{d}",
                    ["raw"]     = unwrapped,
                    ["effects"] = GetItemEffectsByDid(d),
                });
            }
            else
            {
                entries.Add(unwrapped);
            }
        }
        return entries;
    }

    private object DumpActiveEffects()
    {
        var list = new List<Dictionary<string, object?>>();
        var fx = _data!.GetActiveEffects();
        if (fx is null) return list;
        foreach (var ef in fx.Values)
        {
            list.Add(new Dictionary<string, object?>
            {
                ["effectDid"]  = ef.EffectDid,
                ["effectName"] = ResolveName(unchecked((uint)ef.EffectDid)),
                ["effectId"]   = ef.EffectId,
                ["stack"]      = ef.StackSize,
                ["duration"]   = ef.Duration,
                ["appliedAt"]  = ef.ApplicationTimestamp,
                ["isPaused"]   = ef.IsPaused,
                ["properties"] = SafeCall(() => DumpPropertyList(ef.Properties)),
            });
        }
        return list;
    }

    /// <summary>Capture an examined object — the engine hands us the item's
    /// IID and its full property collection in one event.</summary>
    private void CaptureExaminedObject(ulong iid, IPropertyCollection? props)
    {
        if (iid == 0 || props is null) return;

        // After examining, the IEntity for this IID often becomes available —
        // it's the most reliable source of WeenieId.
        uint weenieId = 0;
        try
        {
            var ent = _data?.GetEntity(iid);
            if (ent?.WeenieId.HasValue == true) weenieId = ent.WeenieId.Value;
        }
        catch { }

        string? entityName = null;
        int? itemSlot = null;
        string? defaultSlot = null;
        var dump = DumpPropertyMap(props.Properties);

        foreach (var kvp in props.Properties ?? new Dictionary<uint, IProperty>())
        {
            var p = kvp.Value;
            var pname = p?.PropertyName?.ToString();
            if (pname is null) continue;

            // Backup paths if GetEntity didn't yield a WeenieId.
            if (weenieId == 0 &&
                (pname == "Entity_DataID" || pname == "DataID" ||
                 pname == "WeenieID"      || pname == "Weenie_ID"))
            {
                if (p is IUInt32Property up && up.UInt32Value.HasValue)
                    weenieId = up.UInt32Value.Value;
                else if (p is IInt32Property ip && ip.Int32Value.HasValue)
                    weenieId = unchecked((uint)ip.Int32Value.Value);
            }

            // The slot the item is currently in (Head, Armor, Weapon1, ...).
            if (itemSlot is null && pname == "Inventory_Slot")
            {
                if (p is IInt32Property ip && ip.Int32Value.HasValue) itemSlot = ip.Int32Value.Value;
                else if (p is IUInt32Property up && up.UInt32Value.HasValue) itemSlot = (int)up.UInt32Value.Value;
            }

            // Inventory_DefaultSlot is the most reliable slot indicator on items —
            // it's a BitField32 whose decoded values include the slot name
            // (Head, Eyes, Wrists, Finger1, Trinket, etc.) plus container metas.
            if (defaultSlot is null && pname == "Inventory_DefaultSlot"
                && p is IBitFieldProperty bf && bf.Values is not null)
            {
                foreach (var v in bf.Values)
                {
                    var s = v?.ToString();
                    if (!string.IsNullOrEmpty(s) && !_slotMetaFlags.Contains(s))
                    {
                        defaultSlot = s;
                        break;
                    }
                }
            }

            // First StringInfo property whose name contains "Name" usually wins.
            if (entityName is null &&
                pname.Contains("Name", StringComparison.Ordinal) &&
                p is IStringInfoProperty sip)
            {
                if (sip.IsLiteral && IsRealText(sip.Text))
                    entityName = sip.Text;
                else if (_pm is not null)
                {
                    try
                    {
                        var entry = sip.GetStringEntry(_pm);
                        if (IsRealText(entry?.Value)) entityName = entry!.Value;
                    }
                    catch { }
                }
            }
        }

        var snapshot = new ExaminedItem
        {
            InstanceId  = iid,
            WeenieId    = weenieId,
            EntityName  = entityName,
            Resolved    = weenieId != 0 ? ResolveName(weenieId) : null,
            Properties  = dump,
            ItemSlotRaw = itemSlot,
            DefaultSlot = defaultSlot,
            CapturedUtc = DateTime.UtcNow,
        };
        _examinedItems[iid] = snapshot;
        Log($"examined: iid={iid} weenie={weenieId} slot={defaultSlot ?? itemSlot?.ToString() ?? "?"} name={entityName ?? snapshot.Resolved ?? "?"}");
    }

    /// <summary>Equipped gear: surfaces every item the player has examined this
    /// session, plus any cached-slot IIDs that haven't been examined yet (so
    /// you can see which slots still need a right-click → Examine).</summary>
    private object DumpEquippedFromProps(Dictionary<string, IProperty> playerProps, IEntity player)
    {
        // Build a reverse index: IID -> slot name from the player's CachedSlot
        // properties. Only Armor / Weapon1 / Weapon2 / Ammo are exposed this way;
        // everything else has to come from the item's own Inventory_Slot.
        var iidToSlot = new Dictionary<ulong, string>();
        foreach (var (name, p) in playerProps)
        {
            if (!name.StartsWith("Inventory_CachedSlot_", StringComparison.Ordinal)) continue;
            object? val = UnwrapProperty(p);
            ulong iid = val switch
            {
                ulong u => u, long l => (ulong)l,
                uint u2 => u2,  int i  => (uint)i,
                _ => 0UL,
            };
            if (iid != 0) iidToSlot[iid] = name.Substring("Inventory_CachedSlot_".Length);
        }

        var equipped = new List<Dictionary<string, object?>>();

        // 1. Every examined item in the session cache.
        foreach (var (iid, snap) in _examinedItems)
        {
            // Slot priority:
            //   1. Inventory_DefaultSlot decoded BitField on the item itself
            //      ("Eyes", "Wrists", "Finger1", "Trinket", ...)
            //   2. The player's Inventory_CachedSlot_* mapping
            //   3. The item's Inventory_Slot int decoded against ContainerSlot
            string slotName;
            if (!string.IsNullOrEmpty(snap.DefaultSlot))
            {
                slotName = snap.DefaultSlot;
            }
            else if (iidToSlot.TryGetValue(iid, out var s))
            {
                slotName = s;
            }
            else if (snap.ItemSlotRaw is int raw)
            {
                slotName = Enum.IsDefined(typeof(ContainerSlot), raw)
                    ? ((ContainerSlot)raw).ToString()
                    : $"slot_{raw}";
            }
            else
            {
                slotName = "examined";
            }

            // Active = item carries Inventory_IsEquipped == 1 AND its IID is
            // the one the player's CachedSlot points at (when there is one).
            int? isEquippedRaw = null;
            foreach (var p in snap.Properties)
            {
                if ((string?)p.GetValueOrDefault("name") == "Inventory_IsEquipped")
                {
                    isEquippedRaw = AsInt(p.GetValueOrDefault("value"));
                    break;
                }
            }
            bool isEquipped = isEquippedRaw == 1;
            bool matchesCachedSlot = iidToSlot.ContainsKey(iid);
            string status = (isEquipped, matchesCachedSlot) switch
            {
                (true,  true)  => "active",        // worn AND points at cached slot
                (true,  false) => "active",        // worn (cached slot doesn't track this slot type)
                (false, _)     => "swap-set",      // not currently equipped
            };

            equipped.Add(new Dictionary<string, object?>
            {
                ["slot"]        = slotName,
                ["status"]      = status,
                ["isEquipped"]  = isEquipped,
                ["instanceId"]  = iid,
                ["weenieId"]    = snap.WeenieId,
                ["entityName"]  = snap.EntityName,
                ["resolved"]    = snap.Resolved,
                ["summary"]     = BuildItemSummary(snap.Properties),
                ["capturedUtc"] = snap.CapturedUtc.ToString("o"),
                ["properties"]  = snap.Properties,
                ["source"]      = "examine-cache",
            });
        }

        // 2. Any cached-slot IIDs we haven't seen examined — flag them so the
        // user knows what's still missing.
        foreach (var (iid, slot) in iidToSlot)
        {
            if (_examinedItems.ContainsKey(iid)) continue;
            equipped.Add(new Dictionary<string, object?>
            {
                ["slot"]         = slot,
                ["instanceId"]   = iid,
                ["lookupError"]  = "item not yet examined — right-click → Examine in-game",
                ["source"]       = "missing",
            });
        }

        return equipped;
    }

    private object DumpBuildData()
    {
        try
        {
            var charId = _data!.GetCurrentCharacterId();
            if (charId is null) return new { error = "no character id" };
            var build = _data.GetCharacterBuildData(charId.Value);
            if (build is null) return new { };
            return new Dictionary<string, object?>
            {
                ["properties"] = DumpPropertyMap(build.Properties),
            };
        }
        catch (Exception ex) { Log($"BuildData: {ex.Message}"); return new { error = ex.Message }; }
    }

    /// <summary>Every property the player has, with its name and value — useful
    /// for discovering what the engine actually populates on this character.</summary>
    private static List<Dictionary<string, object?>> DumpRawProps(IPropertyCollection pc)
        => DumpPropertyMap(pc?.Properties);

    // ---- generic helpers ---------------------------------------------------

    /// <summary>Materialise a property collection into a name -> IProperty dict.</summary>
    private static Dictionary<string, IProperty> ToDict(IPropertyCollection? pc)
    {
        var result = new Dictionary<string, IProperty>(StringComparer.Ordinal);
        if (pc?.Properties is null) return result;
        foreach (var kvp in pc.Properties)
        {
            try
            {
                var n = kvp.Value.PropertyName?.ToString();
                if (!string.IsNullOrEmpty(n)) result[n] = kvp.Value;
            }
            catch { }
        }
        return result;
    }

    private static List<Dictionary<string, object?>> DumpPropertyMap(
        IReadOnlyDictionary<uint, IProperty>? props)
    {
        var list = new List<Dictionary<string, object?>>();
        if (props is null) return list;
        foreach (var kvp in props) list.Add(PropToDict(kvp.Value));
        return list;
    }

    private static List<Dictionary<string, object?>> DumpPropertyList(
        IEnumerable<IProperty>? props)
    {
        var list = new List<Dictionary<string, object?>>();
        if (props is null) return list;
        foreach (var p in props) list.Add(PropToDict(p));
        return list;
    }

    private static Dictionary<string, object?> PropToDict(IProperty? p)
    {
        if (p is null) return new();
        try
        {
            return new Dictionary<string, object?>
            {
                ["id"]    = p.PropertyId,
                ["name"]  = p.PropertyName?.ToString(),
                ["type"]  = p.Type.ToString(),
                ["value"] = UnwrapProperty(p),
            };
        }
        catch { return new(); }
    }

    /// <summary>The IProperty itself implements every applicable typed-property
    /// interface — switch on the property, not on its .Value (which falls back
    /// to a hex ToString for unwrapped numeric types). Returns dictionaries
    /// (not anonymous types) for compound shapes so downstream code can
    /// pattern-match against Dictionary&lt;string, object?&gt;.</summary>
    private static object? UnwrapProperty(IProperty p) => p switch
    {
        IEnumProperty       ep  => new Dictionary<string, object?> {
                                       ["enumString"] = ep.EnumString,
                                       ["enumType"]   = ep.EnumType?.ToString() },
        IBitFieldProperty   bf  => new Dictionary<string, object?> {
                                       ["raw"]    = bf.RawValue,
                                       ["values"] = SimpleValue(bf.Values) },
        IStringInfoProperty si  => si.IsLiteral
                                     ? (object?)si.Text
                                     : new Dictionary<string, object?> {
                                           ["key"] = si.Key, ["table"] = si.Table, ["text"] = si.Text },
        IStringProperty     sp  => sp.StringValue,
        IInt32Property      ip  => ip.Int32Value,
        IUInt32Property     uip => uip.UInt32Value,
        IInt64Property      lp  => lp.Int64Value,
        IUInt64Property     ulp => ulp.UInt64Value,
        IFloatProperty      fp  => fp.FloatValue,
        IDoubleProperty     dp  => dp.DoubleValue,
        IByteProperty       bp  => bp.ByteValue,
        IPositionProperty   pp  => new Dictionary<string, object?> {
                                       ["region"] = pp.Region,
                                       ["blockX"] = pp.BlockX,
                                       ["blockY"] = pp.BlockY },
        IArrayProperty      ap  => DumpPropertyList(ap.Properties),
        _                       => SimpleValue(p.Value),
    };

    /// <summary>Convert anything the SDK might hand us into a JSON-safe value.
    /// IProperty.Value often returns the typed-property wrapper itself
    /// (e.g. an IInt32Property whose ToString gives hex) — unwrap those first.</summary>
    private static object? SimpleValue(object? v) => v switch
    {
        null => null,
        string or bool or byte or sbyte or short or ushort or int or uint or
            long or ulong or float or double or decimal or Guid => v,

        // Specific wrappers first — IEnumProperty / IBitFieldProperty extend
        // IUInt32Property, so they have to come before the generic numeric arms.
        IEnumProperty     ep => new { enumString = ep.EnumString, enumType = ep.EnumType?.ToString() },
        IBitFieldProperty bf => new { raw = bf.RawValue, values = SimpleValue(bf.Values) },
        IStringInfoProperty si => si.Text,

        IInt32Property  ip  => ip.Int32Value,
        IUInt32Property uip => uip.UInt32Value,
        IInt64Property  lp  => lp.Int64Value,
        IUInt64Property ulp => ulp.UInt64Value,
        IFloatProperty  fp  => fp.FloatValue,
        IDoubleProperty dp  => dp.DoubleValue,
        IByteProperty   bp  => bp.ByteValue,
        IStringProperty sp  => sp.StringValue,

        IEnumerable e => e.Cast<object?>().Take(64).Select(SimpleValue).ToList(),
        _ => v.ToString(),
    };

    private static bool TryRead(IPropertyCollection pc, uint id, out object? value)
    {
        try { value = pc.GetInt32PropertyValue(id);   return true; } catch { }
        try { value = pc.GetUInt32PropertyValue(id);  return true; } catch { }
        try { value = pc.GetFloatPropertyValue(id);   return true; } catch { }
        try { value = pc.GetUInt64PropertyValue(id);  return true; } catch { }
        value = null; return false;
    }

    private static T? SafeCall<T>(Func<T> f)
    {
        try { return f(); }
        catch (Exception ex) { Log($"section failed: {ex.Message}"); return default; }
    }

    /// <summary>Pull a clean, human-readable "what's on this item" summary out
    /// of the raw property dump: augment slots (with what's placed in them),
    /// sentience filigree (with set names), and item set bonuses.</summary>
    private object BuildItemSummary(List<Dictionary<string, object?>> props)
    {
        // --- AUGMENT SLOTS ---------------------------------------------------
        // Augment_SlotArray  = slot definitions (id, name, accepted types)
        // Augment_Array      = placed augments (slot id -> augment DataID)
        var augmentDefs = new Dictionary<int, Dictionary<string, object?>>();
        var slotArray = props.FirstOrDefault(p => (string?)p.GetValueOrDefault("name") == "Augment_SlotArray");
        foreach (var entry in AsEnumerable(slotArray?.GetValueOrDefault("value")))
        {
            var sub = ExtractStructFields(entry);
            int sid = AsInt(sub.GetValueOrDefault("Augment_SlotID")) ?? 0;
            if (sid == 0) continue;
            augmentDefs[sid] = new Dictionary<string, object?>
            {
                ["slotId"]      = sid,
                ["slotName"]    = sub.GetValueOrDefault("Augment_SlotName"),
                ["description"] = sub.GetValueOrDefault("Augment_SlotDescription"),
                ["accepts"]     = (sub.GetValueOrDefault("Augment_SlotTypes")
                                     as Dictionary<string, object?>)?.GetValueOrDefault("values"),
                ["preslotted"]  = AsInt(sub.GetValueOrDefault("Augment_SlotEntry_Preslotted")),
            };
        }

        var placedArray = props.FirstOrDefault(p => (string?)p.GetValueOrDefault("name") == "Augment_Array");
        foreach (var entry in AsEnumerable(placedArray?.GetValueOrDefault("value")))
        {
            var sub = ExtractStructFields(entry);
            int sid = AsInt(sub.GetValueOrDefault("Augment_SlotID")) ?? 0;
            int did = AsInt(sub.GetValueOrDefault("Augment_DataID")) ?? 0;
            if (sid == 0 || did == 0) continue;
            if (!augmentDefs.TryGetValue(sid, out var def))
                augmentDefs[sid] = def = new Dictionary<string, object?> { ["slotId"] = sid };
            def["augmentDid"]      = did;
            def["augmentName"]     = ResolveName((uint)did);
            def["augmentBinding"]  = (sub.GetValueOrDefault("Augment_BindingLevel")
                                       as Dictionary<string, object?>)?.GetValueOrDefault("enumString");
            // v0.4: fetch the augment's own Examination_Profile_m_aEffect so the
            // canonical "Slotted Effect: +N Type bonus to Stat" strings survive
            // into the dump. Without this, the viewer has to guess bonus types
            // from the augment's display name.
            def["effects"] = GetItemEffectsByDid((uint)did);
        }

        // --- FILIGREE -------------------------------------------------------
        var filigree = new List<Dictionary<string, object?>>();
        for (int i = 1; i <= 16; i++)
        {
            var slot = props.FirstOrDefault(p => (string?)p.GetValueOrDefault("name") == $"Slotted_Filigree_{i}");
            if (slot is null) continue;
            int did = AsInt(slot.GetValueOrDefault("value")) ?? 0;
            if (did == 0) continue;

            var setBonusProp = props.FirstOrDefault(p => (string?)p.GetValueOrDefault("name") == $"FromAugments_SetBonus_{i}");
            var setName = (setBonusProp?.GetValueOrDefault("value") as Dictionary<string, object?>)
                         ?.GetValueOrDefault("enumString")?.ToString();

            filigree.Add(new Dictionary<string, object?>
            {
                ["slot"]    = i,
                ["did"]     = did,
                ["name"]    = ResolveName((uint)did),
                ["setName"] = setName,
                // v0.4: same trick as augments — pull the filigree's own
                // Examination_Profile_m_aEffect so the bonus text survives.
                ["effects"] = GetItemEffectsByDid((uint)did),
            });
        }

        // --- SENTIENCE ------------------------------------------------------
        var sentience = new Dictionary<string, object?>();
        foreach (var keyName in new[] { "AcceptsSentience", "SentientPersonalityName",
                                          "SentientPersonalityDID", "SentientJewelDID",
                                          "SentientXP" })
        {
            var hit = props.FirstOrDefault(p => (string?)p.GetValueOrDefault("name") == keyName);
            if (hit is not null)
            {
                var v = hit.GetValueOrDefault("value");
                sentience[keyName] = v;
                // Resolve DID-shaped values to names.
                if (keyName.EndsWith("DID") && AsInt(v) is int did && did > 1_000_000)
                    sentience[keyName + "Name"] = ResolveName((uint)did);
            }
        }

        // --- SET BONUSES ----------------------------------------------------
        var setBonusCounts = new Dictionary<string, int>();
        foreach (var p in props)
        {
            var name = (string?)p.GetValueOrDefault("name") ?? "";
            if (!name.StartsWith("FromAugments_SetBonus_") &&
                !name.StartsWith("Item_SetBonus_")) continue;
            var s = (p.GetValueOrDefault("value") as Dictionary<string, object?>)
                    ?.GetValueOrDefault("enumString")?.ToString();
            if (string.IsNullOrEmpty(s) || s == "Undef") continue;
            setBonusCounts[s] = setBonusCounts.GetValueOrDefault(s) + 1;
        }

        // --- ASSEMBLE -------------------------------------------------------
        return new Dictionary<string, object?>
        {
            ["augmentSlots"]   = augmentDefs.Values.OrderBy(d => (int)d["slotId"]!).ToList(),
            ["filigree"]       = filigree,
            ["sentience"]      = sentience.Count > 0 ? sentience : null,
            ["setBonusCounts"] = setBonusCounts.Count > 0 ? setBonusCounts : null,
        };
    }

    /// <summary>Convert a raw struct-shaped property's value list into a
    /// name -> value dictionary for easy field lookup.</summary>
    private static Dictionary<string, object?> ExtractStructFields(object? entry)
    {
        var result = new Dictionary<string, object?>();
        if (entry is Dictionary<string, object?> dict)
        {
            foreach (var fld in AsEnumerable(dict.GetValueOrDefault("value"))
                                  .OfType<Dictionary<string, object?>>())
            {
                if (fld.GetValueOrDefault("name") is string n)
                    result[n] = fld.GetValueOrDefault("value");
            }
        }
        return result;
    }

    /// <summary>Iterate any IEnumerable as IEnumerable&lt;object?&gt;, regardless of
    /// element type. C# generic class types aren't covariant, so a runtime
    /// List&lt;Dictionary&lt;...&gt;&gt; doesn't match `is List&lt;object?&gt;` — but it does
    /// match `is IEnumerable`.</summary>
    private static IEnumerable<object?> AsEnumerable(object? v)
    {
        if (v is null) yield break;
        if (v is IEnumerable e && v is not string)
            foreach (var x in e) yield return x;
    }

    private static int? AsInt(object? v) => v switch
    {
        int i      => i,
        uint u     => (int)u,
        long l     => (int)l,
        ulong ul   => (int)ul,
        short s    => s,
        byte b     => b,
        _          => null,
    };

    /// <summary>True if the string looks like an actual translated text rather
    /// than a raw &lt;hex&gt;:&lt;hex&gt; string-table key.</summary>
    private static bool IsRealText(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        // key format is exactly: 8 hex digits, colon, 8 hex digits
        if (s.Length == 17 && s[8] == ':' &&
            s.Take(8).All(IsHex) && s.Skip(9).All(IsHex)) return false;
        return true;
        static bool IsHex(char c) => (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f');
    }

    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
        NullValueHandling     = NullValueHandling.Include,
        MaxDepth              = 16,
    };
}
