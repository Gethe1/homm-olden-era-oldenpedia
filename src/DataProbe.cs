using System;
using System.IO;
using System.Text;
using BepInEx;

namespace OldenPedia
{
    /// <summary>
    /// Scans the Hex.* (and __Generated) assemblies for MessagePack-serializable
    /// data types and dumps their schema to messagepack_schema.txt.
    ///
    /// Olden Era uses MagicOnion + MessagePack, so the unit / spell / faction /
    /// hero definitions are almost certainly [MessagePackObject] DTOs in
    /// Hex.Shared.dll rather than ScriptableObjects. The [Key(n)] index on each
    /// member is part of the serialization contract — the devs can't change it
    /// without breaking saves/netcode — so it is MORE stable across patches than
    /// the (obfuscated) member names. Reading data BY KEY INDEX is therefore the
    /// most patch-proof extraction strategy, and it ignores GUPS obfuscation.
    ///
    /// This is exploratory: IL2CPP reflection can be quirky. Everything is
    /// wrapped defensively; if a type/member can't be read it's skipped and
    /// noted. Send me the output (and any errors) and we map types -> pedia.
    /// </summary>
    public static class DataProbe
    {
        public static void DumpMessagePackSchema()
        {
            try
            {
                var sb = new StringBuilder();
                int typeCount = 0;

                var asms = Il2CppSystem.AppDomain.CurrentDomain.GetAssemblies();
                foreach (var asm in asms)
                {
                    string aname;
                    try { aname = asm.GetName().Name; }
                    catch { continue; }
                    if (string.IsNullOrEmpty(aname)) continue;
                    if (!(aname.StartsWith("Hex") || aname.StartsWith("__Generated"))) continue;

                    Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Il2CppSystem.Type> types;
                    try { types = asm.GetTypes(); }
                    catch { continue; }

                    foreach (var t in types)
                    {
                        if (t == null) continue;
                        if (!HasAttr(t, "MessagePackObject")) continue;
                        typeCount++;
                        DumpType(t, sb);
                    }
                }

                var dir = Path.Combine(Paths.GameRootPath, "BepInEx", "OldenPedia");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "messagepack_schema.txt");
                File.WriteAllText(path,
                    $"# {typeCount} [MessagePackObject] types found in Hex.* / __Generated\n" +
                    "# Members in declaration order; [Key] marks MessagePack-keyed members.\n" +
                    "# Look for a type with combat-stat members (attack/defence/hp/etc.) = units.\n\n" +
                    sb);

                Plugin.Log.LogInfo($"MessagePack schema ({typeCount} types) written to {path}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"DataProbe failed: {ex}");
            }
        }

        private static void DumpType(Il2CppSystem.Type t, StringBuilder sb)
        {
            sb.AppendLine($"== {t.FullName} ==");
            try
            {
                foreach (var p in t.GetProperties())
                {
                    if (p == null) continue;
                    string type = Safe(() => p.PropertyType.Name);
                    sb.AppendLine($"   prop  {KeyMark(p)} {type} {p.Name}");
                }
                foreach (var f in t.GetFields())
                {
                    if (f == null) continue;
                    string type = Safe(() => f.FieldType.Name);
                    sb.AppendLine($"   field {KeyMark(f)} {type} {f.Name}");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"   <member enumeration failed: {ex.Message}>");
            }
            sb.AppendLine();
        }

        private static bool HasAttr(Il2CppSystem.Type t, string contains)
        {
            try
            {
                foreach (var a in t.GetCustomAttributes(false))
                {
                    if (a == null) continue;
                    if (AttrName(a).Contains(contains)) return true;
                }
            }
            catch { }
            return false;
        }

        // "[Key]" if the member carries a MessagePack Key attribute, else blank.
        // We report presence, not the index — members are dumped in declaration
        // order, which for MessagePack array-mode types follows the key order;
        // the exact [Key(n)] is read later from a typed Hex.Shared reference.
        private static string KeyMark(Il2CppSystem.Reflection.MemberInfo m)
        {
            try
            {
                foreach (var a in m.GetCustomAttributes(false))
                {
                    if (a == null) continue;
                    if (AttrName(a).Contains("Key")) return "[Key]";
                }
            }
            catch { }
            return "     ";
        }

        // IL2CPP objects' ToString() defaults to their full type name, so we can
        // identify an attribute's type without calling GetType() — whose return
        // type (System.Type vs Il2CppSystem.Type) differs across interop builds
        // and was the source of the type-mismatch.
        private static string AttrName(Il2CppSystem.Object a)
        {
            try { return a.ToString() ?? ""; } catch { return ""; }
        }

        private static string Safe(Func<string> f)
        {
            try { return f() ?? "?"; } catch { return "?"; }
        }
    }
}
