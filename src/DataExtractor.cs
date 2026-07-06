using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace OldenPedia
{
    /// <summary>
    /// Reads live unit data out of the cjw registry singleton. Navigation is by
    /// clean type/field names + safe FullName matching (no GetGenericArguments).
    /// Collections are walked via ToArray() + Il2CppSystem.Array (NOT the
    /// non-generic IEnumerator, whose boxed struct enumerator faults il2cpp).
    /// Disk-flushed breadcrumbs go to units_progress.txt.
    /// </summary>
    public static class DataExtractor
    {
        // DeclaredOnly(2)|Instance(4)|Static(8)|Public(16)|NonPublic(32) = 62
        private static readonly Il2CppSystem.Reflection.BindingFlags F =
            (Il2CppSystem.Reflection.BindingFlags)62;
        // Same but WITHOUT DeclaredOnly (=60) so inherited members (e.g. base
        // config `id`) resolve on targeted field/property reads.
        internal static readonly Il2CppSystem.Reflection.BindingFlags FR =
            (Il2CppSystem.Reflection.BindingFlags)60;

        private const string UnitLogic = "Hex.Configs.UnitLogicConfig";
        private const string UnitStat = "Hex.Configs.UnitStat";
        private const string UnitView = "Hex.Configs.UnitViewConfig";
        private const string HeroCfg = "Hex.Configs.HeroConfig";
        private const string FractionCfg = "Hex.Configs.FractionConfig";

        private static readonly List<string> _steps = new List<string>();

        /// In-memory copy of the last extraction (one "id | name | …" line per
        /// unit) so the UI can render without re-reading the file.
        public static readonly List<string> Units = new List<string>();

        /// Structured form of each unit for the master–detail UI.
        public class AbilityInfo
        {
            public string Id, Name, Description, IconKey;
        }

        public struct UnitRow
        {
            public string Id, Name, Fraction, Tier, Hp, DmgMin, DmgMax, Off, Def, Spd, Ini;
            public string OwnId, BaseSid, UpgradeSid, Abilities;
            public List<AbilityInfo> AbilityEntries;
        }
        public static readonly List<UnitRow> UnitRows = new List<UnitRow>();

        /// Generic item shown in any category (unit variant, hero, artifact).
        public class PediaItem
        {
            public string Id, Fraction, Subtitle, Display, Description, IconKey, TabLabel;
            public readonly List<string> StatLabels = new List<string>();
            public readonly List<string> StatValues = new List<string>();
            public readonly List<AbilityInfo> Abilities = new List<AbilityInfo>();
            public void Add(string label, string value)
            {
                if (string.IsNullOrEmpty(value) || value == "?" || value == "null") return;
                StatLabels.Add(label); StatValues.Add(value);
            }
        }
        public class ItemFamily { public string Key; public int Tier; public readonly List<PediaItem> Variants = new List<PediaItem>(); }
        public class CatGroup { public string Name; public readonly List<ItemFamily> Families = new List<ItemFamily>(); public int Count; }
        public class Category { public string Name; public bool Grouped; public readonly List<CatGroup> Groups = new List<CatGroup>(); }

        /// Units / Heroes / Artifacts — drives the window dropdown + index.
        public static readonly List<Category> Categories = new List<Category>();

        private static string BuildAbilitiesText(List<AbilityInfo> abilities)
        {
            if (abilities == null || abilities.Count == 0) return "";
            var sb = new StringBuilder();
            for (int i = 0; i < abilities.Count; i++)
            {
                var ability = abilities[i];
                if (ability == null) continue;
                string name = ability.Name;
                string desc = ability.Description;
                if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(desc)) continue;
                if (!string.IsNullOrEmpty(name)) sb.Append("* <b>").Append(name).Append("</b>");
                if (!string.IsNullOrEmpty(desc))
                {
                    if (!string.IsNullOrEmpty(name)) sb.Append(": ");
                    sb.Append(desc);
                }
                sb.AppendLine();
            }
            return sb.ToString().TrimEnd();
        }

        private static List<AbilityInfo> BuildAbilityEntries(Il2CppSystem.Type viewType, Il2CppSystem.Object viewConfig)
        {
            var result = new List<AbilityInfo>();
            if (viewType == null || viewConfig == null) return result;
            try
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                AddAbilityArray(viewType, viewConfig, "abilities", result, seen);
                AddAbilityArray(viewType, viewConfig, "passives", result, seen);
            }
            catch (Exception ex) { Plugin.Log.LogError($"[abilities] view rows: {ex.Message}"); }
            return result;
        }

        private static void AddAbilityArray(Il2CppSystem.Type ownerType, Il2CppSystem.Object owner, string field, List<AbilityInfo> result, HashSet<string> seen)
        {
            var abilities = ReadObjArray(ownerType, owner, field, out var abilityType);
            if (abilityType == null) abilityType = FindType("Hex.Configs.AbilityViewConfig");
            for (int i = 0; i < abilities.Count; i++) AddAbilityView(abilityType, abilities[i], result, seen);
        }

        private static void AddAbilityView(Il2CppSystem.Type abilityType, Il2CppSystem.Object ability, List<AbilityInfo> result, HashSet<string> seen)
        {
            if (abilityType == null || ability == null || result == null || seen == null) return;
            try
            {
                string show = Read(abilityType, ability, "canShowOnUI");
                if (string.Equals(show, "False", StringComparison.OrdinalIgnoreCase)) return;

                string nameKey = Read(abilityType, ability, "name");
                string name = ResolveConfigText(nameKey);
                string desc = ResolveConfigText(Read(abilityType, ability, "description"));
                string extra = ResolveConfigText(Read(abilityType, ability, "additionalDescription"));
                string info = ResolveConfigText(Read(abilityType, ability, "infoDescription"));
                if (!string.IsNullOrEmpty(extra) && !TextEquals(extra, desc)) desc = JoinLines(desc, extra);
                if (!string.IsNullOrEmpty(info) && !TextEquals(info, desc)) desc = JoinLines(desc, info);

                string icon = Read(abilityType, ability, "icon");
                if (icon == "?" || icon == "null") icon = null;
                if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(desc)) return;

                string key = (nameKey ?? "") + "|" + (name ?? "") + "|" + (desc ?? "") + "|" + (icon ?? "");
                if (!seen.Add(key)) return;
                result.Add(new AbilityInfo { Id = nameKey, Name = name, Description = desc, IconKey = icon });
            }
            catch { }
        }

        private static string ResolveConfigText(string key)
        {
            if (string.IsNullOrEmpty(key) || key == "?" || key == "null") return "";
            string value = Localizer.Resolve(key);
            return string.IsNullOrEmpty(value) ? "" : value;
        }

        private static bool TextEquals(string a, string b)
        {
            return string.Equals(a ?? "", b ?? "", StringComparison.Ordinal);
        }

        private static string JoinLines(string first, string second)
        {
            if (string.IsNullOrEmpty(first)) return second ?? "";
            if (string.IsNullOrEmpty(second)) return first;
            return first + "\n" + second;
        }

        private static string FamilyKey(string id)
        {
            if (string.IsNullOrEmpty(id)) return id;
            if (id.EndsWith("_upg_alt", StringComparison.Ordinal)) return id.Substring(0, id.Length - 8);
            if (id.EndsWith("_upg", StringComparison.Ordinal)) return id.Substring(0, id.Length - 4);
            return id;
        }

        // One-time diagnostic: what each secondary category actually resolved to.
        private static void DumpItems(string tag, List<PediaItem> items)
        {
            try
            {
                int n = Math.Min(6, items.Count);
                for (int i = 0; i < n; i++)
                {
                    var it = items[i];
                    int dl = string.IsNullOrEmpty(it.Description) ? 0 : it.Description.Length;
                    Plugin.Log.LogInfo($"[dump] {tag}: id='{it.Id}' name='{it.Display}' descLen={dl} icon='{it.IconKey}'");
                }
            }
            catch { }
        }


        private static int VariantRank(string id)
        {
            if (string.IsNullOrEmpty(id)) return 0;
            if (id.EndsWith("_upg_alt", StringComparison.Ordinal)) return 2;
            if (id.EndsWith("_upg", StringComparison.Ordinal)) return 1;
            return 0;
        }

        private static CatGroup GetGroup(Category cat, Dictionary<string, CatGroup> map, string name)
        {
            if (!map.TryGetValue(name, out var g))
            {
                g = new CatGroup { Name = name };
                map[name] = g; cat.Groups.Add(g);
            }
            return g;
        }

        private static void BuildCategories(List<UnitRow> rows)
        {
            Categories.Clear();
            LangLoader.EnsureLoaded();
            Plugin.Log.LogInfo($"[loc] lang entries={LangLoader.Count}; " +
                               $"skeleton='{Localizer.Name("", "skeleton")}' " +
                               $"sunlight_cavalry='{Localizer.Name("", "sunlight_cavalry")}' " +
                               $"desc.skeleton.len={(Localizer.Resolve("skeleton_narrativeDescription") ?? "").Length}");

            // ----- Units: faction -> family(base+2 upgrades) -----
            var units = new Category { Name = "Units", Grouped = true };
            var umap = new Dictionary<string, CatGroup>();
            foreach (var r in rows)
            {
                string frac = string.IsNullOrEmpty(r.Fraction) ? "?" : r.Fraction;
                var grp = GetGroup(units, umap, frac);
                string key = FamilyKey(r.OwnId);
                ItemFamily fam = null;
                for (int i = 0; i < grp.Families.Count; i++) if (grp.Families[i].Key == key) { fam = grp.Families[i]; break; }
                if (fam == null) { fam = new ItemFamily { Key = key }; grp.Families.Add(fam); }
                bool dup = false;
                for (int i = 0; i < fam.Variants.Count; i++) if (fam.Variants[i].Id == r.OwnId) { dup = true; break; }
                if (dup) continue;

                int tierN = ParseInt(r.Tier);
                if (fam.Tier == 0) fam.Tier = tierN;

                var it = new PediaItem { Id = r.OwnId, Fraction = frac };
                if (r.AbilityEntries != null) it.Abilities.AddRange(r.AbilityEntries);
                string baseName = Localizer.Name(r.Name, r.OwnId);
                it.Display = tierN > 0 ? $"{Roman(tierN)}  {baseName}" : baseName;
                it.Subtitle = Cap(frac) + (tierN > 0 ? $"   ·   Tier {tierN}" : "");
                it.Add("Health", r.Hp);
                it.Add("Damage", $"{r.DmgMin}-{r.DmgMax}");
                it.Add("Offence", r.Off);
                it.Add("Defence", r.Def);
                it.Add("Speed", r.Spd);
                it.Add("Initiative", r.Ini);
                string nd = Localizer.Resolve(r.OwnId + "_narrativeDescription");
                if (string.IsNullOrEmpty(nd)) nd = Localizer.Resolve(r.OwnId + "_description");
                var sb2 = new StringBuilder();
                if (!string.IsNullOrEmpty(nd)) sb2.Append(nd);
                if (!string.IsNullOrEmpty(r.Abilities) && it.Abilities.Count == 0)
                {
                    if (sb2.Length > 0) sb2.Append("\n\n");
                    sb2.Append("<b>Abilities:</b>\n").Append(r.Abilities);
                }
                if (sb2.Length > 0) it.Description = sb2.ToString();
                fam.Variants.Add(it);
            }
            foreach (var g in units.Groups)
            {
                foreach (var fam in g.Families)
                    fam.Variants.Sort((a, b) =>
                    {
                        int ra = VariantRank(a.Id), rb = VariantRank(b.Id);
                        return ra != rb ? ra - rb : string.Compare(a.Id, b.Id, StringComparison.Ordinal);
                    });
                g.Families.Sort((a, b) =>
                {
                    if (a.Tier != b.Tier) return a.Tier - b.Tier;
                    string an = a.Variants.Count > 0 ? a.Variants[0].Display : a.Key;
                    string bn = b.Variants.Count > 0 ? b.Variants[0].Display : b.Key;
                    return string.Compare(an, bn, StringComparison.Ordinal);
                });
                g.Count = 0; foreach (var fam in g.Families) g.Count += fam.Variants.Count;
            }
            Categories.Add(units);

            // ----- Heroes: faction -> hero -----
            var heroes = new Category { Name = "Heroes", Grouped = true };
            var arts = new Category { Name = "Artifacts", Grouped = false };
            var skills = new Category { Name = "Skills", Grouped = false };
            try
            {
            var hitems = ExtractItems(HeroCfg, "name", "description",
                new[] { "Class", "Start level" },
                new[] { "classType", "startLevel" },
                new[] { "{0}", "{0}_hero_name", "{0}_name" },
                new[] { "{0}_description", "{0}_hero_description", "{0}_narrativeDescription" },
                "stats",
                new[] { "offence", "defence", "spellPower", "intelligence" },
                new[] { "Offense", "Defense", "Spell Power", "Intelligence" });
            DumpItems("Heroes", hitems);
            var hmap = new Dictionary<string, CatGroup>();
            foreach (var it in hitems)
            {
                // Capitalize the class type (game stores it lowercase, e.g. "might"/"magic").
                for (int i = 0; i < it.StatLabels.Count; i++)
                    if (it.StatLabels[i] == "Class" && !string.IsNullOrEmpty(it.StatValues[i]))
                        it.StatValues[i] = Cap(it.StatValues[i]);

                string frac = string.IsNullOrEmpty(it.Fraction) ? "?" : it.Fraction;
                var grp = GetGroup(heroes, hmap, frac);
                var fam = new ItemFamily { Key = it.Id }; fam.Variants.Add(it);
                grp.Families.Add(fam); grp.Count++;
            }
            Categories.Add(heroes);

            // ----- Artifacts: scrolls in one group; rest grouped by set; lone -> Other -----
            arts.Grouped = true;
            List<PediaItem> aitems = new List<PediaItem>();
            foreach (var cfg in new[] { "Hex.Configs.ItemConfig", "Hex.Configs.ArtifactConfig", "Hex.Configs.ArtifactLogicConfig" })
            {
                aitems = ExtractItems(cfg, "name", "description",
                    new[] { "Rarity", "Slot", "Cost", "Max level", "Set" },
                    new[] { "rarity", "slot_", "costBase", "maxLevel", "itemSet" },
                    new[] { "{0}_artifact_name", "{0}_name" },
                    new[] { "{0}_artifact_description", "{0}_artifact_alt_description", "{0}_artifact_narrativeDescription", "{0}_description", "{0}_narrativeDescription" },
                    null, null, null, true, 1);
                if (aitems.Count > 0) { Plugin.Log.LogInfo($"Artifacts from {cfg}: {aitems.Count}"); DumpItems("Artifacts", aitems); break; }
            }

            var scrolls = new CatGroup { Name = "Scrolls" };
            var other = new CatGroup { Name = "Other artifacts" };
            var setGroups = new Dictionary<string, CatGroup>();
            var setOrder = new List<string>();

            // Pre-count how many artifacts belong to each set, so the set-bonus
            // header can say "N items in set" using the real count.
            var setItemCounts = new Dictionary<string, int>();
            var setDisplayNames = new Dictionary<string, string>();
            var setBonusTexts = new Dictionary<string, string>();
            foreach (var it in aitems)
            {
                string s = GetStat(it, "Set");
                if (string.IsNullOrEmpty(s) || s == "?" || s == "null" || s == "0") continue;
                setItemCounts.TryGetValue(s, out int c);
                setItemCounts[s] = c + 1;
            }

            foreach (var it in aitems)
            {
                string id = it.Id ?? "";
                if (id.StartsWith("scroll", StringComparison.OrdinalIgnoreCase))
                {
                    // a scroll's name is the spell it casts
                    string spell = id.StartsWith("scroll_") ? id.Substring(7) : id;
                    string sn = Localizer.Resolve(spell + "_name");
                    if (string.IsNullOrEmpty(sn)) sn = Localizer.Resolve("spell_" + spell + "_name");
                    if (string.IsNullOrEmpty(sn)) sn = Localizer.Resolve(spell);
                    if (!string.IsNullOrEmpty(sn)) it.Display = sn;
                    AddLone(scrolls, it);
                    continue;
                }

                string set = GetStat(it, "Set");
                if (!string.IsNullOrEmpty(set) && set != "?" && set != "null" && set != "0")
                {
                    if (!setGroups.TryGetValue(set, out var g))
                    {
                        string sname = Localizer.Resolve(set);
                        if (string.IsNullOrEmpty(sname)) sname = Localizer.Resolve(set + "_item_set");
                        if (string.IsNullOrEmpty(sname)) sname = Localizer.Resolve(set + "_artifactSet_name");
                        if (string.IsNullOrEmpty(sname)) sname = Localizer.Resolve("artifactSet_" + set + "_name");
                        if (string.IsNullOrEmpty(sname)) sname = Localizer.Resolve(set + "_set_name");
                        if (string.IsNullOrEmpty(sname)) sname = Localizer.Resolve(set + "_name");
                        if (string.IsNullOrEmpty(sname)) sname = Cap(set.Replace('_', ' '));
                        g = new CatGroup { Name = sname };
                        setGroups[set] = g; setOrder.Add(set);
                        setDisplayNames[set] = sname;

                        int itemCount = setItemCounts.TryGetValue(set, out int cc) ? cc : 0;
                        var bonusText = BuildSetBonusText(set, itemCount);
                        setBonusTexts[set] = bonusText;
                        if (_setLog < 20)
                        {
                            _setLog++;
                            Plugin.Log.LogInfo($"[setbonus] set='{set}' name='{sname}' items={itemCount} bonusLen={(bonusText?.Length ?? 0)} sample='{Localizer.Resolve(set + "_item_set_description_1")}'");
                        }
                        if (!string.IsNullOrEmpty(bonusText))
                        {
                            var bonusItem = new PediaItem { Id = set + "#setbonus", Display = sname + " - Set Bonus", Description = bonusText };
                            g.Families.Add(new ItemFamily { Key = bonusItem.Id, Variants = { bonusItem } });
                            g.Count++;
                        }
                    }

                    // Also append the set info directly onto THIS artifact's own
                    // page. A separate "Set Bonus" list entry is easy to miss, so
                    // don't rely on it being the only place this shows.
                    if (setBonusTexts.TryGetValue(set, out var sbt) && !string.IsNullOrEmpty(sbt))
                    {
                        string sn = setDisplayNames.TryGetValue(set, out var snm) ? snm : Cap(set.Replace('_', ' '));
                        string note = $"<b>Part of the {sn} set</b>\n{sbt}";
                        it.Description = string.IsNullOrEmpty(it.Description) ? note : it.Description + "\n\n" + note;
                    }

                    AddLone(g, it);
                }
                else AddLone(other, it);
            }

            if (scrolls.Count > 0) arts.Groups.Add(scrolls);
            setOrder.Sort((a, b) => string.Compare(setGroups[a].Name, setGroups[b].Name, StringComparison.OrdinalIgnoreCase));
            foreach (var s in setOrder) arts.Groups.Add(setGroups[s]);
            if (other.Count > 0) arts.Groups.Add(other);
            Categories.Add(arts);

            // ----- Skills: one collapsible GROUP per logical skill (Basic/Upgrade 1/
            // Upgrade 2 as its families — same accordion mechanic as unit factions
            // and artifact sets, collapsed until you click into the skill). A skill
            // that reads identically under Normal/Arena/Campaign is merged into a
            // single group instead of showing near-duplicates 3 times; a genuinely
            // different Arena/Campaign-only version stays separate. -----
            skills.Grouped = true;
            List<PediaItem> sitems = new List<PediaItem>();
            string skillCfgName = null;
            foreach (var cfg in new[] { "Hex.Configs.SkillConfig", "Hex.Configs.SkillLogicConfig",
                                        "Hex.Configs.SkillViewConfig", "Hex.Configs.PerkConfig" })
            {
                sitems = ExtractItems(cfg, "name", "desc",
                    new string[0], new string[0],
                    new[] { "skill_{0}_name", "{0}_skill_name", "sub_skill_{0}_name", "{0}_name" },
                    new[] { "skill_{0}_desc", "skill_{0}_description", "sub_skill_{0}_desc", "{0}_desc", "{0}_description" },
                    null, null, null, false, 2, false, "skillType");
                if (sitems.Count > 0) { skillCfgName = cfg; Plugin.Log.LogInfo($"Skills from {cfg}: {sitems.Count}"); DumpItems("Skills", sitems); break; }
            }

            var skillIdx = new Dictionary<string, Il2CppSystem.Object>();
            Il2CppSystem.Type skillCfgTypeOut = null;
            if (!string.IsNullOrEmpty(skillCfgName)) skillIdx = BuildIdIndex(skillCfgName, out skillCfgTypeOut);

            var built = new List<BuiltSkill>();
            var seenTypesLog = new HashSet<string>();
            foreach (var it in sitems)
            {
                if (it.Id != null && it.Id.StartsWith("skill_pseudo", StringComparison.Ordinal)) continue;
                if (it.Id != null && it.Id.StartsWith("arena_skill", StringComparison.OrdinalIgnoreCase)) continue;
                if (it.Id != null && it.Id.StartsWith("campaign_skill", StringComparison.OrdinalIgnoreCase)) continue;
                string typeName = string.IsNullOrEmpty(it.Fraction) ? "Normal" : Cap(it.Fraction);
                if (seenTypesLog.Add(typeName)) Plugin.Log.LogInfo($"[skills] type seen: '{typeName}' (raw='{it.Fraction}')");

                List<ItemFamily> fams;
                if (skillIdx != null && skillCfgTypeOut != null && skillIdx.TryGetValue(it.Id, out var rawObj))
                    fams = BuildSkillFamilies(skillCfgTypeOut, rawObj, it);
                else
                    fams = new List<ItemFamily> { new ItemFamily { Key = it.Id, Variants = { it } } };

                built.Add(new BuiltSkill { DispName = it.Display, Sig = SkillSignature(fams), TypeName = typeName, Fams = fams });
            }

            // Merge entries with the same name AND identical generated content
            // (same signature) across type buckets; keep real differences separate.
            var mergedMap = new Dictionary<string, List<ItemFamily>>();
            var mergedTypes = new Dictionary<string, List<string>>();
            var mergeOrder = new List<string>();
            foreach (var b in built)
            {
                string key = b.DispName + "||" + b.Sig;
                if (!mergedMap.ContainsKey(key))
                {
                    mergedMap[key] = b.Fams; mergedTypes[key] = new List<string>(); mergeOrder.Add(key);
                }
                mergedTypes[key].Add(b.TypeName);
            }

            var skillGroups = new List<CatGroup>();
            foreach (var key in mergeOrder)
            {
                var fams = mergedMap[key];
                string dispName = fams.Count > 0 && fams[0].Variants.Count > 0 ? fams[0].Variants[0].Display : "Skill";
                var grp = new CatGroup { Name = dispName };
                foreach (var f in fams) { grp.Families.Add(f); grp.Count++; }
                skillGroups.Add(grp);
                Plugin.Log.LogInfo($"[skills] '{dispName}' <- [{string.Join(",", mergedTypes[key].ToArray())}]");
            }
            skillGroups.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            foreach (var g in skillGroups) skills.Groups.Add(g);
            Categories.Add(skills);
            }
            catch (Exception ex) { Plugin.Log.LogError($"BuildCategories secondary: {ex}"); }

            if (!Categories.Contains(heroes)) Categories.Add(heroes);
            if (!Categories.Contains(arts)) Categories.Add(arts);
            if (!Categories.Contains(skills)) Categories.Add(skills);

            Plugin.Log.LogInfo($"Categories: Units({Count(units)}) Heroes({Count(heroes)}) " +
                               $"Artifacts({Count(arts)}) Skills({Count(skills)})");
            WriteEffectsDump();
        }

        private static int Count(Category c) { int n = 0; foreach (var g in c.Groups) n += g.Count; return n; }

        // Generic catalog reader: id, display name, description + labelled fields.
        private static int _iconSamples = 0;
        private static int _bonusLog = 0;
        private static int _buffLog = 0;
        private static int _setLog = 0;
        private sealed class SetBonusTier
        {
            public int RequiredItems;
            public string DescKey;
            public readonly List<string> Values = new List<string>();
        }

        private static readonly Dictionary<string, List<SetBonusTier>> _setBonusTierCache = new Dictionary<string, List<SetBonusTier>>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _setBonusTierMissLogged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _setBonusLineMissLogged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Collects value strings from explicit parameter members and simple scalar arrays/lists.
        private static bool TryGetMemberValue(Il2CppSystem.Type ownerType, Il2CppSystem.Object obj, string name, out Il2CppSystem.Object value, out Il2CppSystem.Type memberType)
        {
            value = null;
            memberType = null;
            try
            {
                if (ownerType == null || obj == null || string.IsNullOrEmpty(name)) return false;

                var p = ownerType.GetProperty(name, FR);
                if (p != null)
                {
                    value = p.GetValue(obj);
                    memberType = p.PropertyType;
                    return value != null;
                }

                var f = ownerType.GetField(name, FR);
                if (f != null)
                {
                    value = f.GetValue(obj);
                    memberType = f.FieldType;
                    return value != null;
                }
            }
            catch { }
            return false;
        }
        private static bool TryReadArrayLike(Il2CppSystem.Object value, Il2CppSystem.Type valueType, out Il2CppSystem.Array arr)
        {
            arr = null;
            try
            {
                if (value == null) return false;
                arr = value.TryCast<Il2CppSystem.Array>();
                if (arr != null) return true;
                if (valueType == null) return false;
                var toArray = valueType.GetMethod("ToArray", F);
                if (toArray == null) return false;
                var noArgs = new Il2CppReferenceArray<Il2CppSystem.Object>(0);
                var arrObj = toArray.Invoke(value, noArgs);
                arr = arrObj != null ? arrObj.TryCast<Il2CppSystem.Array>() : null;
                return arr != null;
            }
            catch { return false; }
        }

        private static bool ForEachIl2CppArrayElement(Il2CppSystem.Object value, Il2CppSystem.Type memberType, Action<object> visit)
        {
            if (value == null || visit == null) return false;
            string declared = Safe(() => memberType.FullName);
            string runtime = Safe(() => value.GetType().FullName);
            bool declaredArray = !string.IsNullOrEmpty(declared) &&
                (declared.IndexOf("[]", StringComparison.Ordinal) >= 0 ||
                 declared.IndexOf("Array", StringComparison.OrdinalIgnoreCase) >= 0);
            bool runtimeArray = !string.IsNullOrEmpty(runtime) && runtime.IndexOf("Array", StringComparison.OrdinalIgnoreCase) >= 0;
            bool stringArray =
                (!string.IsNullOrEmpty(declared) &&
                 (declared.IndexOf("String[]", StringComparison.Ordinal) >= 0 ||
                  declared.IndexOf("Il2CppStringArray", StringComparison.Ordinal) >= 0)) ||
                (!string.IsNullOrEmpty(runtime) && runtime.IndexOf("Il2CppStringArray", StringComparison.Ordinal) >= 0);

            if (stringArray)
            {
                try
                {
                    var strings = new Il2CppStringArray(value.Pointer);
                    int length = strings.Length;
                    for (int i = 0; i < length; i++) visit(strings[i]);
                    return true;
                }
                catch { return false; }
            }

            bool referenceArray =
                (!string.IsNullOrEmpty(declared) && declared.IndexOf("Il2CppReferenceArray", StringComparison.Ordinal) >= 0) ||
                (!string.IsNullOrEmpty(runtime) && runtime.IndexOf("Il2CppReferenceArray", StringComparison.Ordinal) >= 0) ||
                (declaredArray && !stringArray) || (runtimeArray && !stringArray);
            if (referenceArray)
            {
                try
                {
                    var refs = new Il2CppReferenceArray<Il2CppSystem.Object>(value.Pointer);
                    int length = refs.Length;
                    for (int i = 0; i < length; i++) visit(refs[i]);
                    return true;
                }
                catch { return false; }
            }

            return false;
        }
        private static Il2CppSystem.Type ResolveArrayElementType(Il2CppSystem.Type memberType)
        {
            try
            {
                var elem = memberType != null ? memberType.GetElementType() : null;
                if (elem != null) return elem;
            }
            catch { }

            string fullName = Safe(() => memberType.FullName);
            int start = !string.IsNullOrEmpty(fullName) ? fullName.IndexOf("Hex.", StringComparison.Ordinal) : -1;
            while (start >= 0)
            {
                int end = start;
                while (end < fullName.Length)
                {
                    char c = fullName[end];
                    if (!(char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '`')) break;
                    end++;
                }
                if (end > start)
                {
                    var t = FindType(fullName.Substring(start, end - start));
                    if (t != null) return t;
                }
                start = fullName.IndexOf("Hex.", start + 1, StringComparison.Ordinal);
            }
            return null;
        }

        private static bool IsSimpleScalarType(string typeName)
        {
            switch (typeName)
            {
                case "String":
                case "Int32":
                case "UInt32":
                case "Int16":
                case "Int64":
                case "UInt16":
                case "UInt64":
                case "Single":
                case "Double":
                case "Boolean":
                case "Byte":
                case "SByte":
                    return true;
                default:
                    return false;
            }
        }

        private static string SimpleValueText(Il2CppSystem.Object value)
        {
            if (value == null) return null;
            string typeName = Safe(() => value.GetType().Name);
            return Boxed(value, typeName);
        }

        private static bool AppendSimpleValues(Il2CppSystem.Object value, Il2CppSystem.Type valueType, List<string> vals, int depth = 0)
        {
            if (value == null || vals == null || depth > 2) return false;
            bool wrapperAny = false;
            if (ForEachIl2CppArrayElement(value, valueType, element =>
            {
                if (AppendSimpleManagedValue(element, vals, depth + 1)) wrapperAny = true;
            })) return wrapperAny;

            Il2CppSystem.Array arr;
            if (TryReadArrayLike(value, valueType, out arr))
            {
                bool any = false;
                int n = 0;
                try { n = arr.Length; } catch { return false; }
                for (int i = 0; i < n; i++)
                {
                    Il2CppSystem.Object e;
                    try { e = arr.GetValue(i); } catch { continue; }
                    if (AppendSimpleValues(e, null, vals, depth + 1)) any = true;
                }
                return any;
            }



            string typeName = Safe(() => value.GetType().Name);
            if (!IsSimpleScalarType(typeName)) return false;
            string s = SimpleValueText(value);
            if (string.IsNullOrEmpty(s) || s == "null" || s == "?") return false;
            vals.Add(s);
            return true;
        }

        private static bool AppendSimpleManagedValue(object value, List<string> vals, int depth)
        {
            if (value == null || vals == null || depth > 2) return false;
            if (value is string s)
            {
                if (string.IsNullOrEmpty(s) || s == "null" || s == "?") return false;
                vals.Add(s);
                return true;
            }
            if (value is Il2CppSystem.Object obj)
                return AppendSimpleValues(obj, null, vals, depth);

            var type = value.GetType();
            switch (Type.GetTypeCode(type))
            {
                case TypeCode.Boolean:
                case TypeCode.Byte:
                case TypeCode.SByte:
                case TypeCode.Int16:
                case TypeCode.UInt16:
                case TypeCode.Int32:
                case TypeCode.UInt32:
                case TypeCode.Int64:
                case TypeCode.UInt64:
                case TypeCode.Single:
                case TypeCode.Double:
                case TypeCode.Decimal:
                    vals.Add(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
                    return true;
                default:
                    return false;
            }
        }

        private static List<string> ReadBonusParams(Il2CppSystem.Type ownerType, Il2CppSystem.Object obj)
        {
            var vals = new List<string>();
            try
            {
                if (ownerType == null || obj == null) return vals;

                if (TryGetMemberValue(ownerType, obj, "parameters", out var directParams, out var directParamsType))
                    AppendSimpleValues(directParams, directParamsType, vals);

                if (vals.Count > 0) return vals;

                var bonuses = ReadObjArray(ownerType, obj, "bonuses", out var bonusType);
                if (bonusType == null) bonusType = FindType("Hex.Configs.BonusConfig");
                foreach (var bonus in bonuses)
                {
                    if (bonus == null || bonusType == null) continue;
                    if (TryGetMemberValue(bonusType, bonus, "parameters", out var paramsObj, out var paramsType))
                        AppendSimpleValues(paramsObj, paramsType, vals);
                }
            }
            catch { }
            return vals;
        }
        // Fills {0},{1},... from vals, in order. If a placeholder is immediately
        // followed by '%' in the template AND the value looks like a 0-1 fraction,
        // it's converted to a whole-number percent first (0.4 -> "40", not "0.40")
        // — confirmed bug: "Music Sheet" showed "+0.40%" instead of the game's own
        // "+40%" because the raw stored fraction was inserted verbatim.
        private static readonly Regex RxPlaceholder = new Regex(@"\{(\d+)\}(%?)", RegexOptions.Compiled);

        private static string FillPlaceholders(string desc, List<string> vals)
        {
            if (string.IsNullOrEmpty(desc) || vals == null || vals.Count == 0) return desc;
            return RxPlaceholder.Replace(desc, m =>
            {
                if (!int.TryParse(m.Groups[1].Value, out int idx) || idx < 0 || idx >= vals.Count)
                    return m.Value; // leave unresolved indices alone; caught later by StripUnresolvedLines
                string raw = vals[idx];
                bool isPercent = m.Groups[2].Value == "%";
                if (isPercent && float.TryParse(raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out float f) && Math.Abs(f) <= 1f)
                    return ((int)Math.Round(f * 100f)) + "%";
                return raw + m.Groups[2].Value;
            });
        }

        // Reads an array-of-objects field (e.g. bonuses, parametersPerLevel) into a list
        // of elements plus the element type, for further field reads.
        internal static List<Il2CppSystem.Object> ReadObjArray(Il2CppSystem.Type ownerType, Il2CppSystem.Object obj, string field, out Il2CppSystem.Type elemType)
        {
            elemType = null;
            var list = new List<Il2CppSystem.Object>();
            try
            {
                if (!TryGetMemberValue(ownerType, obj, field, out var value, out var memberType)) return list;
                elemType = ResolveArrayElementType(memberType);
                Il2CppSystem.Array arr;
                if (TryReadArrayLike(value, memberType, out arr))
                {
                    for (int i = 0; i < arr.Length; i++) { var e = arr.GetValue(i); if (e != null) list.Add(e); }
                    return list;
                }

                ForEachIl2CppArrayElement(value, memberType, element =>
                {
                    if (element is Il2CppSystem.Object e && e != null) list.Add(e);
                });
            }
            catch { }
            return list;
        }
        private static string Trunc(string s, int n) { if (string.IsNullOrEmpty(s)) return ""; return s.Length > n ? s.Substring(0, n) : s; }

        // Writes a detailed dump of artifact bonuses and skill parametersPerLevel so
        // effect text + skill structure can be implemented against real data.
        private static readonly StringBuilder _fx = new StringBuilder();
        private static int _fxArt, _fxSkill;

        private static void DumpArtifact(Il2CppSystem.Type type, Il2CppSystem.Object o, PediaItem it)
        {
            if (_fxArt >= 10) return; _fxArt++;
            try
            {
                var bonuses = ReadObjArray(type, o, "bonuses", out var bt);
                _fx.Append($"ART {it.Id} | name='{it.Display}' | descKey='{Read(type, o, "description")}' | desc='{Trunc(it.Description, 90)}' | bonuses={bonuses.Count}");
                for (int i = 0; i < bonuses.Count && i < 4; i++)
                {
                    string btype = bt != null ? Read(bt, bonuses[i], "type") : "?";
                    var pars = bt != null ? ReadStringArray(bt, bonuses[i], "parameters") : new List<string>();
                    _fx.Append($"  [{btype}: {string.Join(",", pars.ToArray())}]");
                }
                _fx.AppendLine();
            }
            catch (Exception ex) { _fx.AppendLine("ART dump err: " + ex.Message); }
        }

        private static void DumpSkill(Il2CppSystem.Type type, Il2CppSystem.Object o, PediaItem it)
        {
            if (_fxSkill >= 12) return;
            if (Read(type, o, "isPseudoSkill") == "True") return; // skip internal pseudo skills
            _fxSkill++;
            try
            {
                var pars = ReadObjArray(type, o, "parametersPerLevel", out var spt);
                _fx.AppendLine($"SKILL {it.Id} | name='{it.Display}' | desc='{Trunc(it.Description, 90)}' | isPseudo={Read(type, o, "isPseudoSkill")} | params={pars.Count}");
                for (int i = 0; i < pars.Count && i < 8; i++)
                {
                    string pn = spt != null ? Read(spt, pars[i], "name") : "?";
                    string pd = spt != null ? Read(spt, pars[i], "desc") : "?";
                    var pb = spt != null ? ReadObjArray(spt, pars[i], "bonuses", out var pbt2) : new List<Il2CppSystem.Object>();
                    var subs = spt != null ? ReadStringArray(spt, pars[i], "subSkills") : new List<string>();
                    _fx.AppendLine($"   L{i}: name='{pn}'->'{Trunc(Localizer.Resolve(pn), 40)}' desc='{pd}'->'{Trunc(Localizer.Resolve(pd), 50)}' bonuses={pb.Count} subs=[{string.Join(",", subs.ToArray())}]");
                }
            }
            catch (Exception ex) { _fx.AppendLine("SKILL dump err: " + ex.Message); }
        }

        internal static void WriteEffectsDump()
        {
            try
            {
                string dir = System.IO.Path.Combine(BepInEx.Paths.GameRootPath, "BepInEx", "OldenPedia");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "effects_dump.txt"), _fx.ToString());
                Plugin.Log.LogInfo($"[fx] effects_dump.txt written (art={_fxArt} skill={_fxSkill})");
            }
            catch (Exception ex) { Plugin.Log.LogError("[fx] write: " + ex.Message); }
        }

        // Generic: reads every entry of a config catalog (by full type name) into an
        // id -> object map, for looking up things like perks or abilities by id
        // outside the normal per-category extraction. Returns empty (not null) if
        // the catalog doesn't exist, so callers never need a null check.
        private static Dictionary<string, Il2CppSystem.Object> _catalogCache = new Dictionary<string, Il2CppSystem.Object>();
        private static readonly Dictionary<string, Dictionary<string, Il2CppSystem.Object>> _idIndexCache = new Dictionary<string, Dictionary<string, Il2CppSystem.Object>>();

        internal static Dictionary<string, Il2CppSystem.Object> BuildIdIndex(string cfgFullName, out Il2CppSystem.Type type)
        {
            type = FindType(cfgFullName);
            if (_idIndexCache.TryGetValue(cfgFullName, out var cached)) return cached;
            var map = new Dictionary<string, Il2CppSystem.Object>();
            try
            {
                var reg = Registry(out var inst);
                if (reg != null && inst != null && type != null)
                {
                    var cf = FindCatalogField(reg, cfgFullName);
                    if (cf != null)
                    {
                        var listObj = ReadValuesList(cf.FieldType, cf.GetValue(inst), out var lt);
                        if (listObj != null)
                            foreach (var o in Iterate(listObj, lt))
                            {
                                if (o == null) continue;
                                string id = Read(type, o, "id");
                                if (!string.IsNullOrEmpty(id) && id != "?" && id != "null") map[id] = o;
                            }
                    }
                }
            }
            catch (Exception ex) { Plugin.Log.LogError($"[idx] {cfgFullName}: {ex.Message}"); }
            _idIndexCache[cfgFullName] = map;
            return map;
        }

        internal static List<PediaItem> ExtractItems(string cfgFullName, string nameField, string descField,
                                                    string[] statLabels, string[] statFields,
                                                    string[] namePatterns, string[] descPatterns,
                                                    string nestedStatField = null, string[] nestedFields = null, string[] nestedLabels = null,
                                                    bool fillBonuses = false, int dumpKind = 0, bool enrichSkill = false,
                                                    string groupField = "fraction")
        {
            var outp = new List<PediaItem>();
            try
            {
                var reg = Registry(out var inst);
                if (reg == null || inst == null) return outp;
                var type = FindType(cfgFullName);
                if (type == null) return outp;
                var cf = FindCatalogField(reg, cfgFullName);
                if (cf == null) return outp;
                var listObj = ReadValuesList(cf.FieldType, cf.GetValue(inst), out var lt);
                if (listObj == null) return outp;
                foreach (var o in Iterate(listObj, lt))
                {
                    if (o == null) continue;
                    string id = Read(type, o, "id");
                    if (string.IsNullOrEmpty(id) || id == "?" || id == "null") continue;

                    var it = new PediaItem { Id = id, Fraction = Read(type, o, groupField) };
                    if (it.Fraction == "?" || it.Fraction == "null") it.Fraction = "";

                    string nameKey = !string.IsNullOrEmpty(nameField) ? Read(type, o, nameField) : null;
                    if (nameKey == "?" || nameKey == "null") nameKey = null;
                    if (string.IsNullOrEmpty(nameKey)) nameKey = Read(type, o, "name_");
                    if (nameKey == "?" || nameKey == "null") nameKey = null;
                    it.Display = Localizer.NameByPatterns(nameKey, id, namePatterns);

                    string dKey = !string.IsNullOrEmpty(descField) ? Read(type, o, descField) : null;
                    if (dKey == "?" || dKey == "null") dKey = null;
                    string d = Localizer.DescByPatterns(dKey, id, descPatterns);
                    if (!string.IsNullOrEmpty(d)) it.Description = d;

                    // Artifacts: the loc description is flavor; the real effect is the
                    // bonuses array (e.g. heroStat spellPower 12 -> "+12 Spell Power").
                    if (fillBonuses) BuildBonusStats(type, o, it);

                    int n = Math.Min(statLabels != null ? statLabels.Length : 0, statFields != null ? statFields.Length : 0);
                    for (int i = 0; i < n; i++) it.Add(statLabels[i], Read(type, o, statFields[i]));

                    if (!string.IsNullOrEmpty(nestedStatField)) AppendNestedStats(it, type, o, nestedStatField, nestedFields, nestedLabels);

                    string icon = Read(type, o, "icon");
                    if (icon == "?" || icon == "null") icon = null;
                    if (!string.IsNullOrEmpty(icon)) it.IconKey = icon;
                    if (_iconSamples < 6 && !string.IsNullOrEmpty(icon))
                    { Plugin.Log.LogInfo($"[icon] {id} icon='{icon}'"); _iconSamples++; }

                    if (dumpKind == 1) DumpArtifact(type, o, it);
                    else if (dumpKind == 2) DumpSkill(type, o, it);

                    outp.Add(it);
                }
            }
            catch (Exception ex) { Plugin.Log.LogError($"ExtractItems({cfgFullName}): {ex.Message}"); }
            return outp;
        }

        // Turns an item's bonuses array into readable effect lines. heroStat bonuses
        // become stat rows (+N / +N%); every OTHER bonus type is also shown — via a
        // BuffConfig lookup when the last parameter is a buff id, else as a labeled
        // raw fallback so nothing is silently dropped. Also fills {0}/{1}.. in the
        // item's own description text from the same bonus values, since some
        // "_description" keys carry the effect itself, not just flavor.
        private static readonly HashSet<string> _seenBonusTypes = new HashSet<string>();

        private static void BuildBonusStats(Il2CppSystem.Type type, Il2CppSystem.Object o, PediaItem it)
        {
            try
            {
                var bonuses = ReadObjArray(type, o, "bonuses", out var bt);
                if (bt == null || bonuses.Count == 0) return;
                int idx = 0;
                var extra = new List<string>();
                var allNumericVals = new List<string>();

                foreach (var b in bonuses)
                {
                    string btype = Read(bt, b, "type");
                    var pars = ReadStringArray(bt, b, "parameters");

                    // One-time comprehensive catalog of every distinct bonus type this
                    // game uses, with a real example — tells us what still needs its
                    // own formatter instead of the generic fallback.
                    if (_seenBonusTypes.Add(btype ?? "?"))
                        Plugin.Log.LogInfo($"[bonustype] '{btype}' example params=[{string.Join(",", pars.ToArray())}] item={it.Id}");

                    foreach (var p in pars)
                        if (float.TryParse(p, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _))
                            allNumericVals.Add(p);

                    if (btype == "heroStat" && pars.Count >= 2)
                    {
                        it.StatLabels.Insert(idx, PrettyStat(pars[0]));
                        it.StatValues.Insert(idx, FormatStatVal(pars[0], pars[1]));
                        idx++;
                        continue;
                    }

                    if (pars.Count == 0) continue;

                    // Non-stat effects (marketplace, spawns, battle buffs, etc.)
                    // reference a buff id in the last parameter. Its own name/desc
                    // key isn't a fixed pattern derived from the id (confirmed via
                    // the ability probe — a buff's cngp/cngq are computed, not
                    // "<id>_name"), so look the buff up and read those directly.
                    string refId = pars[pars.Count - 1];
                    string txt = null;
                    var buffIdx = BuildIdIndex("Hex.Configs.BuffConfig", out var buffType);
                    if (buffType != null && buffIdx.TryGetValue(refId, out var buffObj))
                    {
                        string nameKey = Read(buffType, buffObj, "cngp");
                        string descKey = Read(buffType, buffObj, "cngq");
                        string nm = Localizer.Resolve(nameKey);
                        string ds = Localizer.Resolve(descKey);
                        if (_buffLog < 10)
                        {
                            _buffLog++;
                            Plugin.Log.LogInfo($"[buff] type='{btype}' refId='{refId}' nameKey='{nameKey}'->'{nm}' descKey='{descKey}'->'{ds}'");
                        }
                        txt = !string.IsNullOrEmpty(ds) ? ds : nm;
                    }
                    if (string.IsNullOrEmpty(txt))
                        txt = Localizer.Resolve(refId + "_description") ?? Localizer.Resolve(refId + "_desc")
                                     ?? Localizer.Resolve(refId + "_name") ?? Localizer.Resolve(refId);

                    // This bonus's own values may fill {0}/{1} in the resolved text.
                    if (!string.IsNullOrEmpty(txt) && txt.IndexOf('{') >= 0) txt = FillPlaceholders(txt, pars);

                    // Never show the player raw internal data (unresolved text, or a
                    // description still carrying an unfilled {0}) — omit it instead.
                    // An incomplete/garbled line is worse than a missing one.
                    if (!string.IsNullOrEmpty(txt) && txt.IndexOf('{') < 0 && !extra.Contains(txt)) extra.Add(txt);
                }

                if (extra.Count > 0)
                {
                    string effects = string.Join("\n", extra.ToArray());
                    it.Description = string.IsNullOrEmpty(it.Description) ? effects : effects + "\n\n" + it.Description;
                }

                // Some "_description" keys carry the mechanical effect itself (with
                // {0}/{1} placeholders), not just flavor — fill them from the same
                // bonus values now that we've collected them.
                if (!string.IsNullOrEmpty(it.Description) && it.Description.IndexOf('{') >= 0 && allNumericVals.Count > 0)
                    it.Description = FillPlaceholders(it.Description, allNumericVals);

                // Never leave a raw unfilled {0} visible — drop just that line
                // rather than show broken text or hide the whole description.
                if (!string.IsNullOrEmpty(it.Description) && it.Description.IndexOf('{') >= 0)
                    it.Description = StripUnresolvedLines(it.Description);
            }
            catch (Exception ex) { Plugin.Log.LogError($"[bonus] BuildBonusStats {it?.Id}: {ex.Message}"); }
        }

        // Drops any line still containing an unfilled {N} placeholder — showing
        // raw template syntax to the player is worse than omitting that one line.
        private static string StripUnresolvedLines(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var lines = text.Split('\n');
            var kept = new List<string>();
            foreach (var line in lines)
                if (line.IndexOf('{') < 0) kept.Add(line);
            return string.Join("\n", kept.ToArray()).Trim();
        }

        private static string PrettyStat(string s)
        {
            bool per = s != null && s.EndsWith("Per", StringComparison.Ordinal);
            string baseName = per ? s.Substring(0, s.Length - 3) : s;
            switch (baseName)
            {
                case "offence": return "Attack";
                case "defence": return "Defense";
                case "spellPower": return "Spell Power";
                case "intelligence": return "Intelligence";
                case "moral": return "Morale";
                case "luck": return "Luck";
                case "initiative": return "Initiative";
                case "speed": return "Speed";
                default: return Cap(baseName);
            }
        }

        private static string FormatStatVal(string stat, string val)
        {
            bool per = stat != null && stat.EndsWith("Per", StringComparison.Ordinal);
            if (per && float.TryParse(val, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out float f))
            {
                int pct = (int)Math.Round(f * 100f);
                return (pct >= 0 ? "+" : "") + pct + "%";
            }
            if (!string.IsNullOrEmpty(val) && val[0] != '-' && val[0] != '+') return "+" + val;
            return val;
        }

        // Subskill option values live in Hex.Configs.SubSkillConfig. The skill
        // tiers reference subskill ids from SkillParameter.subSkills, and the
        // display/description loc keys come from the referenced SubSkillConfig.
        private const string SubSkillCatalog = "Hex.Configs.SubSkillConfig";
        private static readonly Dictionary<string, List<string>> _subSkillValCache = new Dictionary<string, List<string>>();
        private static bool _subSkillCatalogLogged;

        private static bool TryGetSubSkillConfig(string subSkillId, out Il2CppSystem.Type subType, out Il2CppSystem.Object subObj)
        {
            subType = null;
            subObj = null;
            if (string.IsNullOrEmpty(subSkillId)) return false;
            var idx = BuildIdIndex(SubSkillCatalog, out subType);
            if (!_subSkillCatalogLogged)
            {
                _subSkillCatalogLogged = true;
                Plugin.Log.LogInfo($"[subskill] catalog '{SubSkillCatalog}': {idx.Count} entries");
            }
            return subType != null && idx.TryGetValue(subSkillId, out subObj) && subObj != null;
        }

        private static List<string> FindSubSkillBonusValues(string subSkillId)
        {
            if (_subSkillValCache.TryGetValue(subSkillId, out var cached)) return cached;
            var result = new List<string>();
            if (TryGetSubSkillConfig(subSkillId, out var subType, out var subObj))
            {
                result = ReadBonusParams(subType, subObj);
                Plugin.Log.LogInfo($"[subskill] {subSkillId} values=[{string.Join(",", result.ToArray())}]");
            }
            _subSkillValCache[subSkillId] = result;
            return result;
        }

        private static string ResolveSubSkillName(string subSkillId)
        {
            if (!TryGetSubSkillConfig(subSkillId, out var subType, out var subObj)) return null;
            string key = Read(subType, subObj, "name");
            string value = Localizer.Resolve(key);
            return !string.IsNullOrEmpty(value) ? value : null;
        }

        private static string ResolveSubSkillDescription(string subSkillId)
        {
            if (!TryGetSubSkillConfig(subSkillId, out var subType, out var subObj)) return null;
            string key = Read(subType, subObj, "desc");
            string value = Localizer.Resolve(key);
            if (!string.IsNullOrEmpty(value) && value.IndexOf('{') >= 0)
            {
                var vals = FindSubSkillBonusValues(subSkillId);
                if (vals.Count > 0) value = FillPlaceholders(value, vals);
            }
            if (!string.IsNullOrEmpty(value) && value.IndexOf('{') >= 0) value = StripUnresolvedLines(value);
            return value;
        }
        // Content fingerprint for a skill's generated families. Used to merge
        // Normal/Arena/Campaign copies only when their generated text is identical.
        // Plain classes are used instead of value tuples because named tuples can
        // require compiler-synthesized attributes unavailable in this game's
        // reference assemblies.
        private class BuiltSkill
        {
            public string DispName, Sig, TypeName;
            public List<ItemFamily> Fams;
        }

        private static string SkillSignature(List<ItemFamily> fams)
        {
            var sb = new StringBuilder();
            foreach (var f in fams)
            {
                sb.Append("F:").Append(f.Key ?? "").Append(':').Append(f.Variants.Count).Append('|');
                foreach (var v in f.Variants)
                {
                    sb.Append("V:").Append(v.Id ?? "").Append(':')
                      .Append(v.TabLabel ?? "").Append(':')
                      .Append(v.Display ?? "").Append(':')
                      .Append(v.Description ?? "").Append('|');
                }
            }
            return sb.ToString();
        }
        private class SkillOption { public string Name, Desc; }

        private static List<ItemFamily> BuildSkillFamilies(Il2CppSystem.Type type, Il2CppSystem.Object o, PediaItem baseItem)
        {
            var result = new List<ItemFamily>();
            try
            {
                var levels = ReadObjArray(type, o, "parametersPerLevel", out var spt);
                if (spt == null || levels.Count == 0) { result.Add(new ItemFamily { Key = baseItem.Id, Variants = { baseItem } }); return result; }

                var levelFam = new ItemFamily { Key = baseItem.Id };
                for (int i = 0; i < levels.Count; i++)
                {
                    var lv = levels[i];
                    string levelName = Localizer.Resolve(Read(spt, lv, "name"));
                    string desc = BuildSkillLevelDescription(spt, lv);
                    if (string.IsNullOrEmpty(desc) && i == 0) desc = baseItem.Description;
                    string icon = Read(spt, lv, "icon");
                    if (icon == "?" || icon == "null") icon = baseItem.IconKey;

                    levelFam.Variants.Add(new PediaItem
                    {
                        Id = baseItem.Id + "#level" + (i + 1),
                        Fraction = baseItem.Fraction,
                        Display = baseItem.Display,
                        Subtitle = baseItem.Subtitle,
                        Description = desc,
                        IconKey = string.IsNullOrEmpty(icon) ? baseItem.IconKey : icon,
                        TabLabel = SkillLevelTabLabel(levelName, i)
                    });
                }
                result.Add(levelFam);

                for (int i = 0; i < levels.Count; i++)
                {
                    var lv = levels[i];
                    var subs = ReadStringArray(spt, lv, "subSkills");
                    if (subs.Count == 0) continue;

                    string levelName = Localizer.Resolve(Read(spt, lv, "name"));
                    string tierLabel = SkillLevelTabLabel(levelName, i);
                    string tierDesc = BuildSkillLevelDescription(spt, lv);

                    var seen = new HashSet<string>();
                    var options = new List<SkillOption>();
                    foreach (var sub in subs)
                    {
                        if (!seen.Add(sub)) continue;
                        string pn = ResolveSubSkillName(sub);
                        if (string.IsNullOrEmpty(pn)) continue;
                        string pd = ResolveSubSkillDescription(sub);
                        string optDesc = pd;
                        if (!string.IsNullOrEmpty(tierDesc))
                            optDesc = string.IsNullOrEmpty(optDesc) ? tierDesc : tierDesc + "\n\n" + optDesc;
                        options.Add(new SkillOption { Name = pn, Desc = optDesc });
                    }

                    if (options.Count == 0)
                    {
                        if (_skillTierLog < 40) { _skillTierLog++; Plugin.Log.LogInfo($"[skilltier] {baseItem.Id} {tierLabel}: rawSubs={subs.Count} resolved=0 -> SKIPPED (subskill config/name unresolved)"); }
                        continue;
                    }

                    if (_skillTierLog < 40) { _skillTierLog++; Plugin.Log.LogInfo($"[skilltier] {baseItem.Id} {tierLabel}: rawSubs={subs.Count} resolved={options.Count}"); }
                    var optionFam = new ItemFamily { Key = baseItem.Id + "#subskills" + (i + 1) };
                    foreach (var opt in options)
                    {
                        optionFam.Variants.Add(new PediaItem
                        {
                            Id = baseItem.Id + "#" + tierLabel + "#" + opt.Name,
                            Fraction = baseItem.Fraction,
                            Display = baseItem.Display + " - " + tierLabel + " Subskills",
                            Subtitle = baseItem.Subtitle,
                            Description = opt.Desc,
                            IconKey = baseItem.IconKey,
                            TabLabel = opt.Name
                        });
                    }
                    result.Add(optionFam);
                }
            }
            catch (Exception ex) { Plugin.Log.LogError($"[skilltier] {baseItem?.Id}: {ex.Message}"); }
            if (result.Count == 0) result.Add(new ItemFamily { Key = baseItem.Id, Variants = { baseItem } });
            return result;
        }
        private static string BuildSkillLevelDescription(Il2CppSystem.Type spt, Il2CppSystem.Object level)
        {
            string desc = Localizer.Resolve(Read(spt, level, "desc"));
            if (string.IsNullOrEmpty(desc)) return desc;
            if (desc.IndexOf('{') >= 0)
            {
                var vals = GetLevelValues(spt, level);
                if (vals.Count > 0) desc = FillPlaceholders(desc, vals);
            }
            if (!string.IsNullOrEmpty(desc) && desc.IndexOf('{') >= 0) desc = StripUnresolvedLines(desc);
            return desc;
        }

        private static string SkillLevelTabLabel(string levelName, int index)
        {
            if (!string.IsNullOrEmpty(levelName))
            {
                if (levelName.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return "Basic";
                if (levelName.StartsWith("Advanced ", StringComparison.OrdinalIgnoreCase)) return "Advanced";
                if (levelName.StartsWith("Expert ", StringComparison.OrdinalIgnoreCase)) return "Expert";
                return levelName;
            }
            if (index == 0) return "Basic";
            if (index == 1) return "Advanced";
            if (index == 2) return "Expert";
            return "Level " + (index + 1);
        }

        private static List<string> GetLevelValues(Il2CppSystem.Type spt, Il2CppSystem.Object level)
        {
            var vals = new List<string>();
            try
            {
                var bonuses = ReadObjArray(spt, level, "bonuses", out var lbt);
                if (lbt == null) return vals;
                foreach (var b in bonuses)
                    AppendNumericValues(ReadStringArray(lbt, b, "parameters"), vals);
            }
            catch { }
            return vals;
        }

        private static string FormatNum(float f)
        {
            if (Math.Abs(f) < 1f && f != 0f) return ((int)Math.Round(f * 100f)).ToString();
            if (f == Math.Floor(f)) return ((int)f).ToString();
            return f.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static void AddLone(CatGroup g, PediaItem it)
        {
            var fam = new ItemFamily { Key = it.Id };
            fam.Variants.Add(it);
            g.Families.Add(fam);
            g.Count++;
        }

        // Artifact set bonuses are native ItemSetConfig.bonuses tiers. Each tier
        // carries its own requiredItemsAmount, desc loc key, and heroBonuses.
        private static int _setValLog = 0;
        private static int _skillTierLog = 0;

        private static string BuildSetBonusText(string set, int itemCount)
        {
            var tiers = FindSetBonusTiers(set);
            if (tiers.Count == 0) return "";

            var lines = new List<string>();
            for (int i = 0; i < tiers.Count; i++)
            {
                var tier = tiers[i];
                string d = Localizer.Resolve(tier.DescKey);
                if (string.IsNullOrEmpty(d))
                {
                    if (_setBonusLineMissLogged.Add(set + ":missing:" + i))
                        Plugin.Log.LogInfo($"[setbonus] set='{set}' tier={i + 1} has no resolved description key '{tier.DescKey}'");
                    continue;
                }
                if (d.IndexOf('{') >= 0 && tier.Values.Count > 0) d = FillPlaceholders(d, tier.Values);
                if (d.IndexOf('{') >= 0)
                {
                    if (_setBonusLineMissLogged.Add(set + ":placeholder:" + i))
                        Plugin.Log.LogInfo($"[setbonus] set='{set}' tier={i + 1} still has unresolved placeholder text; dropping unresolved lines");
                    d = StripUnresolvedLines(d);
                }
                if (string.IsNullOrEmpty(d)) continue;
                lines.Add("* <b>" + SetBonusTierLabel(tier, i, tiers.Count) + ":</b> " + d);
            }

            if (lines.Count == 0) return "";
            var sb = new StringBuilder();
            if (itemCount > 0) sb.Append(itemCount).Append(itemCount == 1 ? " item in set" : " items in set").AppendLine();
            foreach (var line in lines) sb.AppendLine(line);
            return sb.ToString().TrimEnd();
        }
        private static string SetBonusTierLabel(SetBonusTier tier, int index, int total)
        {
            if (tier != null && tier.RequiredItems > 0) return tier.RequiredItems + "-item bonus";
            bool isLast = index == total - 1;
            return total == 1 ? "Full set bonus" : (isLast ? "Full set bonus" : "Partial set bonus");
        }

        private static void AppendNumericValues(List<string> source, List<string> target)
        {
            if (source == null || target == null) return;
            for (int i = 0; i < source.Count; i++)
            {
                string value = source[i];
                if (float.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _))
                    target.Add(value);
            }
        }

        private static List<SetBonusTier> FindSetBonusTiers(string set)
        {
            if (_setBonusTierCache.TryGetValue(set, out var cached)) return cached;

            var result = new List<SetBonusTier>();
            try
            {
                var idx = BuildIdIndex("Hex.Configs.ItemSetConfig", out var stype);
                if (_setValLog < 8) Plugin.Log.LogInfo($"[setval] catalog 'Hex.Configs.ItemSetConfig': {idx.Count} entries");
                if (idx.Count > 0 && stype != null && idx.TryGetValue(set, out var sobj))
                {
                    var bonusObjs = ReadObjArray(stype, sobj, "bonuses", out var tierType);
                    if (tierType == null) tierType = FindType("Hex.Configs.ItemSetBonus");
                    foreach (var bonusObj in bonusObjs)
                    {
                        if (bonusObj == null || tierType == null) continue;
                        var tier = new SetBonusTier();
                        tier.DescKey = Read(tierType, bonusObj, "desc");
                        int req;
                        if (int.TryParse(Read(tierType, bonusObj, "requiredItemsAmount"), out req)) tier.RequiredItems = req;

                        var heroBonuses = ReadObjArray(tierType, bonusObj, "heroBonuses", out var bonusType);
                        if (bonusType == null) bonusType = FindType("Hex.Configs.BonusConfig");
                        foreach (var heroBonus in heroBonuses)
                        {
                            if (heroBonus == null || bonusType == null) continue;
                            var vals = ReadBonusParams(bonusType, heroBonus);
                            AppendNumericValues(vals, tier.Values);
                        }

                        if (!string.IsNullOrEmpty(tier.DescKey) || tier.RequiredItems > 0 || tier.Values.Count > 0)
                            result.Add(tier);
                    }
                }
            }
            catch { }

            if (result.Count == 0 && _setBonusTierMissLogged.Add(set))
                Plugin.Log.LogInfo($"[setval] set='{set}' -> none: no ItemSetConfig.bonuses tiers found");
            if (_setValLog < 8)
            {
                _setValLog++;
                var parts = new List<string>();
                for (int i = 0; i < result.Count; i++)
                    parts.Add($"{result[i].RequiredItems}:{result[i].DescKey}=[{string.Join(",", result[i].Values.ToArray())}]");
                Plugin.Log.LogInfo($"[setval] set='{set}' -> ItemSetConfig tiers={result.Count}: {string.Join("; ", parts.ToArray())}");
            }
            _setBonusTierCache[set] = result;
            return result;
        }

        private static string GetStat(PediaItem it, string label)
        {
            if (it == null || it.StatLabels == null) return null;
            for (int i = 0; i < it.StatLabels.Count && i < it.StatValues.Count; i++)
                if (it.StatLabels[i] == label) return it.StatValues[i];
            return null;
        }

        internal static string Cap(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return char.ToUpper(s[0]) + s.Substring(1);
        }

        private static int ParseInt(string s)
        {
            return int.TryParse(s, out var n) ? n : 0;
        }

        private static readonly string[] _roman =
            { "", "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X", "XI", "XII" };
        private static string Roman(int n)
        {
            if (n <= 0) return "";
            return n < _roman.Length ? _roman[n] : n.ToString();
        }

        // Reads an array-of-config field and returns each element's id.
        // Reads a String[] field (e.g. bonus parameters, subSkills) as plain strings.
        internal static List<string> ReadStringArray(Il2CppSystem.Type ownerType, Il2CppSystem.Object obj, string field)
        {
            var outp = new List<string>();
            try
            {
                if (TryGetMemberValue(ownerType, obj, field, out var value, out var memberType))
                    AppendSimpleValues(value, memberType, outp);
            }
            catch { }
            return outp;
        }
        private static List<string> ReadIdArray(Il2CppSystem.Type ownerType, Il2CppSystem.Object obj,
                                                 string field, Il2CppSystem.Type elemType)
        {
            var outp = new List<string>();
            try
            {
                if (ownerType == null || elemType == null) return outp;
                var f = ownerType.GetField(field, FR);
                if (f == null) return outp;
                var arrObj = f.GetValue(obj);
                if (arrObj == null) return outp;
                var arr = arrObj.TryCast<Il2CppSystem.Array>();
                if (arr == null) return outp;
                int n = arr.Length;
                for (int i = 0; i < n; i++)
                {
                    var e = arr.GetValue(i);
                    if (e == null) continue;
                    string id = Read(elemType, e, "id");
                    if (string.IsNullOrEmpty(id) || id == "?" || id == "null") continue;
                    if (!outp.Contains(id)) outp.Add(id);
                }
            }
            catch { }
            return outp;
        }

        // ---- unit visual recon: fetch the UnitViewConfig for an id ----
        public static Il2CppSystem.Type ViewType => FindType(UnitView);

        private static Il2CppSystem.Type _regType;
        private static Il2CppSystem.Object _regInst;
        internal static Il2CppSystem.Type Registry(out Il2CppSystem.Object inst)
        {
            if (_regType != null && _regInst != null) { inst = _regInst; return _regType; }
            _regType = FindRegistry(out _regInst);
            inst = _regInst;
            return _regType;
        }

        public static Il2CppSystem.Object GetViewConfig(string ownId)
        {
            try
            {
                var reg = Registry(out var inst);
                if (reg == null || inst == null) return null;
                var vt = FindType(UnitView);
                var cf = FindCatalogField(reg, UnitView);
                if (vt == null || cf == null) return null;
                var listObj = ReadValuesList(cf.FieldType, cf.GetValue(inst), out var lt);
                if (listObj == null) return null;
                foreach (var v in Iterate(listObj, lt))
                {
                    if (v == null) continue;
                    if (Read(vt, v, "id") == ownId) return v;
                }
            }
            catch (Exception ex) { Plugin.Log.LogError($"GetViewConfig: {ex.Message}"); }
            return null;
        }

        /// Runs extraction once if we have no data yet (e.g. first window open).
        public static void EnsureLoaded()
        {
            if (LangLoader.TryUpgradeToGameSettings())
            {
                Plugin.Log.LogInfo("[lang] language upgraded to the game's own live setting — rebuilding pedia data");
                Units.Clear(); UnitRows.Clear(); Categories.Clear();
            }
            if (Units.Count == 0) { try { DumpUnits(); } catch { } }
        }

        public static void DumpUnits()
        {
            _steps.Clear();
            var sb = new StringBuilder();
            try
            {
                Step("start");

                var registry = FindRegistry(out var instance);
                if (registry == null) { Fail("registry not found"); return; }
                Step($"registry = {Safe(() => registry.FullName)}, instance={(instance != null)}");
                sb.AppendLine($"registry = {registry.FullName}");
                if (instance == null)
                {
                    Fail($"registry {Safe(() => registry.FullName)} found but singleton null — run F3 inside a loaded game");
                    return;
                }
                Step("instance read");

                var cfgType = FindType(UnitLogic);
                var statsType = FindType(UnitStat);
                var viewType = FindType(UnitView);
                Step($"types: cfg={(cfgType != null)} stats={(statsType != null)} view={(viewType != null)}");
                if (cfgType == null) { Fail("UnitLogicConfig type not found"); return; }

                Step("building name map");
                var names = BuildNameMap(registry, instance, viewType);
                Step($"names built: {names.Count}");
                var viewConfigs = BuildViewConfigMap(registry, instance, viewType);
                Step($"view configs built: {viewConfigs.Count}");
                sb.AppendLine($"view-config names loaded: {names.Count} (iterated {_viewCount}); view rows={viewConfigs.Count}");
                sb.AppendLine();

                var catField = FindCatalogField(registry, UnitLogic);
                if (catField == null) { Fail("unit catalog field not found"); return; }
                Step("catalog field found");
                var catObj = catField.GetValue(instance);
                Step("catalog obj read");

                var listObj = ReadValuesList(catField.FieldType, catObj, out var listType);
                if (listObj == null) { Fail("unit values list not found"); return; }
                Step("values list read");

                var rows = new List<string>();
                var srows = new List<UnitRow>();
                foreach (var u in Iterate(listObj, listType))
                {
                    if (u == null) continue;
                    if (rows.Count == 0) Step("units: first item");

                    string baseSid = Read(cfgType, u, "baseSid");
                    string ownId = Read(cfgType, u, "id");
                    string upg = Read(cfgType, u, "upgradeSid");
                    string id = ownId;       // ownId is the true unique sid
                    string fraction = Read(cfgType, u, "fraction");
                    string tier = Read(cfgType, u, "tier");

                    string hp = "?", dmin = "?", dmax = "?", off = "?", def = "?", spd = "?", init = "?";
                    var statsObj = ReadObj(cfgType, u, "stats");
                    if (statsObj != null && statsType != null)
                    {
                        hp = Read(statsType, statsObj, "hp");
                        dmin = Read(statsType, statsObj, "damageMin");
                        dmax = Read(statsType, statsObj, "damageMax");
                        off = Read(statsType, statsObj, "offence");
                        def = Read(statsType, statsObj, "defence");
                        spd = Read(statsType, statsObj, "speed");
                        init = Read(statsType, statsObj, "initiative");
                    }

                    var abilityEntries = new List<AbilityInfo>();
                    if (viewConfigs.TryGetValue(ownId ?? "", out var viewConfig) || viewConfigs.TryGetValue(id ?? "", out viewConfig))
                        abilityEntries = BuildAbilityEntries(viewType, viewConfig);
                    string abilitiesText = BuildAbilitiesText(abilityEntries);

                    string nm;
                    if (!names.TryGetValue(id ?? "", out nm)) names.TryGetValue(ownId ?? "", out nm);
                    rows.Add($"{id} | {nm} | {fraction} | {tier} | {hp} | {dmin}-{dmax} | {off} | {def} | {spd} | {init}");
                    srows.Add(new UnitRow
                    {
                        Id = id, Name = nm ?? "", Fraction = fraction, Tier = tier,
                        Hp = hp, DmgMin = dmin, DmgMax = dmax, Off = off, Def = def, Spd = spd, Ini = init,
                        OwnId = ownId, BaseSid = baseSid, UpgradeSid = upg,
                        Abilities = abilitiesText,
                        AbilityEntries = abilityEntries
                    });
                    if (rows.Count % 50 == 0) Step($"units {rows.Count}");
                }

                Step($"done: {rows.Count}");
                Units.Clear();
                Units.AddRange(rows);
                UnitRows.Clear();
                UnitRows.AddRange(srows);
                BuildCategories(srows);
                sb.AppendLine($"units: {rows.Count}");
                sb.AppendLine("# id | name | fraction | tier | hp | dmgMin-dmgMax | off | def | speed | init");
                sb.AppendLine();
                foreach (var r in rows) sb.AppendLine(r);

                sb.AppendLine();
                sb.AppendLine("# RAW upgrade relations: ownId | baseSid | upgradeSid");
                foreach (var s in srows)
                    sb.AppendLine($"{s.OwnId} | {s.BaseSid} | {s.UpgradeSid}");

                Write(sb.ToString());
                Plugin.Log.LogInfo($"Units dumped: {rows.Count}");
            }
            catch (Exception ex)
            {
                Step($"EXCEPTION: {ex.Message}");
                sb.AppendLine($"\nEXCEPTION: {ex}");
                Write(sb.ToString());
                Plugin.Log.LogError($"DumpUnits failed: {ex}");
            }
        }

        private static int _viewCount;

        private static Dictionary<string, string> BuildNameMap(Il2CppSystem.Type reg, Il2CppSystem.Object inst, Il2CppSystem.Type viewType)
        {
            var map = new Dictionary<string, string>();
            _viewCount = 0;
            try
            {
                if (viewType == null) { Step("nm: no viewType"); return map; }
                var vf = FindCatalogField(reg, UnitView);
                if (vf == null) { Step("nm: no view catalog field"); return map; }
                var vObj = vf.GetValue(inst);
                var vList = ReadValuesList(vf.FieldType, vObj, out var vListType);
                Step($"nm: vList={(vList != null)}; iterating");
                int c = 0;
                foreach (var v in Iterate(vList, vListType))
                {
                    if (v == null) continue;
                    if (c == 0) Step("nm: first item, reading id");
                    string id = Read(viewType, v, "id");
                    if (c == 0) Step($"nm: first id={id}, reading name_");
                    string name = Read(viewType, v, "name_");
                    if (c == 0) Step($"nm: first name={name}");
                    if (!string.IsNullOrEmpty(id) && id != "?" && id != "null") map[id] = name;
                    if (++c % 50 == 0) Step($"nm: {c}");
                }
                _viewCount = c;
                Step($"nm: iterated {c}");
            }
            catch (Exception ex) { Step($"nm: EX {ex.Message}"); }
            return map;
        }

        private static Dictionary<string, Il2CppSystem.Object> BuildViewConfigMap(Il2CppSystem.Type reg, Il2CppSystem.Object inst, Il2CppSystem.Type viewType)
        {
            var map = new Dictionary<string, Il2CppSystem.Object>();
            try
            {
                if (viewType == null) return map;
                var vf = FindCatalogField(reg, UnitView);
                if (vf == null) return map;
                var vObj = vf.GetValue(inst);
                var vList = ReadValuesList(vf.FieldType, vObj, out var vListType);
                foreach (var v in Iterate(vList, vListType))
                {
                    if (v == null) continue;
                    string id = Read(viewType, v, "id");
                    if (!string.IsNullOrEmpty(id) && id != "?" && id != "null" && !map.ContainsKey(id)) map[id] = v;
                }
            }
            catch (Exception ex) { Step($"vcfg: EX {ex.Message}"); }
            return map;
        }
        // ---- iteration via ToArray() + Il2CppSystem.Array (no enumerator) ----

        internal static IEnumerable<Il2CppSystem.Object> Iterate(Il2CppSystem.Object listObj, Il2CppSystem.Type listType)
        {
            Il2CppSystem.Array arr = null;
            try
            {
                if (listObj != null && listType != null)
                {
                    var toArray = listType.GetMethod("ToArray", F);
                    if (toArray != null)
                    {
                        var noArgs = new Il2CppReferenceArray<Il2CppSystem.Object>(0);
                        var arrObj = toArray.Invoke(listObj, noArgs);
                        arr = arrObj != null ? arrObj.TryCast<Il2CppSystem.Array>() : null;
                    }
                }
            }
            catch (Exception ex) { Step($"iter: ToArray EX {ex.Message}"); arr = null; }

            if (arr == null) { Step("iter: no array"); yield break; }

            int n;
            try { n = arr.Length; } catch { yield break; }
            for (int i = 0; i < n; i++)
            {
                Il2CppSystem.Object item;
                try { item = arr.GetValue(i); } catch { continue; }
                yield return item;
            }
        }

        // ---- navigation (FullName string matching only) ----

        private static Il2CppSystem.Type FindRegistry(out Il2CppSystem.Object instance)
        {
            instance = null;
            Il2CppSystem.Type fallback = null;
            foreach (var t in HexTypes())
            {
                if (t == null || !MatchesRegistry(t)) continue;
                var inst = ReadStaticSelf(t);
                if (inst != null) { instance = inst; return t; }
                if (fallback == null) fallback = t;
            }
            return fallback;
        }

        private static bool MatchesRegistry(Il2CppSystem.Type t)
        {
            string owner = Safe(() => t.FullName);
            if (owner.Length == 0) return false;
            bool self = false, u = false, h = false, fr = false;
            try
            {
                foreach (var f in t.GetFields(F))
                {
                    if (f == null) continue;
                    string ftn = Safe(() => f.FieldType.FullName);
                    if (f.IsStatic && ftn == owner) self = true;
                    if (ftn.IndexOf(UnitLogic, StringComparison.Ordinal) >= 0) u = true;
                    if (ftn.IndexOf(HeroCfg, StringComparison.Ordinal) >= 0) h = true;
                    if (ftn.IndexOf(FractionCfg, StringComparison.Ordinal) >= 0) fr = true;
                }
            }
            catch { return false; }
            return self && u && h && fr;
        }

        private static Il2CppSystem.Object ReadStaticSelf(Il2CppSystem.Type reg)
        {
            string owner = Safe(() => reg.FullName);
            foreach (var f in reg.GetFields(F))
            {
                if (f == null || !f.IsStatic) continue;
                if (Safe(() => f.FieldType.FullName) == owner)
                { try { return f.GetValue(null); } catch { return null; } }
            }
            return null;
        }

        internal static Il2CppSystem.Reflection.FieldInfo FindCatalogField(Il2CppSystem.Type reg, string cfgFullName)
        {
            foreach (var f in reg.GetFields(F))
            {
                if (f == null || f.IsStatic) continue;
                if (Safe(() => f.FieldType.FullName).IndexOf(cfgFullName, StringComparison.Ordinal) >= 0)
                    return f;
            }
            return null;
        }

        // The catalog has exactly one List member (its values). Returns the list
        // object and (out) the List type so Iterate can call ToArray on it.
        internal static Il2CppSystem.Object ReadValuesList(Il2CppSystem.Type catType, Il2CppSystem.Object catObj, out Il2CppSystem.Type listType)
        {
            listType = null;
            Step($"rvl: GetFields on {Safe(() => catType.FullName)}");
            try
            {
                foreach (var f in catType.GetFields(F))
                {
                    if (f == null) continue;
                    if (Safe(() => f.FieldType.FullName).IndexOf("List`1", StringComparison.Ordinal) >= 0)
                    { try { listType = f.FieldType; return f.GetValue(catObj); } catch { } }
                }
            }
            catch { }
            try
            {
                foreach (var p in catType.GetProperties(F))
                {
                    if (p == null) continue;
                    if (Safe(() => p.PropertyType.FullName).IndexOf("List`1", StringComparison.Ordinal) >= 0)
                    { try { listType = p.PropertyType; return p.GetValue(catObj); } catch { } }
                }
            }
            catch { }
            Step("rvl: no list found");
            return null;
        }

        internal static Il2CppSystem.Type FindType(string fullName)
        {
            foreach (var t in HexTypes())
                if (t != null && Safe(() => t.FullName) == fullName) return t;
            return null;
        }

        internal static IEnumerable<Il2CppSystem.Type> HexTypes()
        {
            foreach (var asm in Il2CppSystem.AppDomain.CurrentDomain.GetAssemblies())
            {
                string an; bool ok = true;
                try { an = asm.GetName().Name; } catch { an = null; ok = false; }
                if (!ok || string.IsNullOrEmpty(an) || !an.StartsWith("Hex")) continue;

                Il2CppReferenceArray<Il2CppSystem.Type> types = null;
                try { types = asm.GetTypes(); } catch { types = null; }
                if (types == null) continue;
                foreach (var t in types) yield return t;
            }
        }

        // ---- value readers ----

        internal static string Read(Il2CppSystem.Type type, Il2CppSystem.Object obj, string name)
        {
            try
            {
                if (type == null) return "?";
                var f = type.GetField(name, FR);
                if (f != null) return Boxed(f.GetValue(obj), Safe(() => f.FieldType.Name));
                var p = type.GetProperty(name, FR);
                if (p != null) return Boxed(p.GetValue(obj), Safe(() => p.PropertyType.Name));
                return "?";
            }
            catch { return "err"; }
        }

        // Value types box into Il2CppSystem.Object; ToString() on the box returns
        // its pointer, so unbox primitives to their real value.
        private static string Boxed(Il2CppSystem.Object v, string typeName)
        {
            if (v == null) return "null";
            switch (typeName)
            {
                case "Int32": return v.Unbox<int>().ToString();
                case "UInt32": return v.Unbox<uint>().ToString();
                case "Int16": return v.Unbox<short>().ToString();
                case "Int64": return v.Unbox<long>().ToString();
                case "Single": return v.Unbox<float>().ToString();
                case "Double": return v.Unbox<double>().ToString();
                case "Boolean": return v.Unbox<bool>().ToString();
                case "Byte": return v.Unbox<byte>().ToString();
                default: return v.ToString(); // strings, enums, reference types
            }
        }

        // Reads named numeric fields from a nested stat object (e.g.
        // HeroConfig.stats -> offence/defence/spellPower/intelligence), showing
        // zeros too so a 0 in a primary stat is visible rather than hidden.
        private static void AppendNestedStats(PediaItem it, Il2CppSystem.Type parentType, Il2CppSystem.Object o,
                                              string field, string[] subFields, string[] subLabels)
        {
            try
            {
                if (parentType == null || subFields == null) return;
                var f = parentType.GetField(field, FR);
                if (f == null) return;
                var statObj = f.GetValue(o);
                if (statObj == null) return;
                var statType = f.FieldType;
                if (statType == null) return;
                for (int i = 0; i < subFields.Length; i++)
                {
                    var sf = statType.GetField(subFields[i], FR);
                    if (sf == null) continue;
                    string label = (subLabels != null && i < subLabels.Length) ? subLabels[i] : Cap(subFields[i]);
                    try
                    {
                        string tn = Safe(() => sf.FieldType.Name);
                        if (tn == "Int32") { it.StatLabels.Add(label); it.StatValues.Add(sf.GetValue(statObj).Unbox<int>().ToString()); }
                        else if (tn == "Single") { it.StatLabels.Add(label); it.StatValues.Add(sf.GetValue(statObj).Unbox<float>().ToString("0.##")); }
                    }
                    catch { }
                }
            }
            catch { }
        }

        internal static Il2CppSystem.Object ReadObj(Il2CppSystem.Type type, Il2CppSystem.Object obj, string name)
        {
            try
            {
                if (type == null) return null;
                var f = type.GetField(name, FR);
                if (f != null) return f.GetValue(obj);
                var p = type.GetProperty(name, FR);
                if (p != null) return p.GetValue(obj);
                return null;
            }
            catch { return null; }
        }

        internal static string Safe(Func<string> f) { try { return f() ?? ""; } catch { return ""; } }

        // ---- breadcrumbs + output ----

        private static void Step(string s)
        {
            _steps.Add($"{DateTime.Now:HH:mm:ss} {s}");
            try
            {
                var dir = Path.Combine(Paths.GameRootPath, "BepInEx", "OldenPedia");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "units_progress.txt"),
                    $"# OldenPedia {Plugin.Version} progress\n" + string.Join("\n", _steps));
            }
            catch { }
        }

        private static void Fail(string why)
        {
            Step($"FAIL: {why}");
            Write($"# OldenPedia {Plugin.Version} unit dump FAILED: {why}\n");
            Plugin.Log.LogError($"DumpUnits: {why}");
        }

        private static void Write(string body)
        {
            try
            {
                var dir = Path.Combine(Paths.GameRootPath, "BepInEx", "OldenPedia");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "units_dump.txt"),
                    $"# OldenPedia {Plugin.Version} unit dump — {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n\n" + body);
            }
            catch (Exception ex) { Plugin.Log.LogError($"write failed: {ex}"); }
        }
    }
}
