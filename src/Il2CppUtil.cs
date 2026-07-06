using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace OldenPedia
{
    internal static class Il2CppUtil
    {
        // Real runtime IL2CPP type name of an object.
        //
        // We deliberately do NOT use GetType() here: on this interop build it
        // returns the managed WRAPPER type, so every object comes back as
        // "UnityEngine.Object". Instead we read the il2cpp class straight off the
        // native object pointer, which gives the true runtime type (including
        // obfuscated game types).
        public static string RuntimeTypeName(Il2CppObjectBase o)
        {
            try
            {
                if (o == null || o.Pointer == IntPtr.Zero) return "<null>";

                var klass = IL2CPP.il2cpp_object_get_class(o.Pointer);
                if (klass == IntPtr.Zero) return "<noclass>";

                string name = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(klass)) ?? "?";
                string ns = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_namespace(klass)) ?? "";
                return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
            }
            catch
            {
                return "<unknown>";
            }
        }
    }
}
