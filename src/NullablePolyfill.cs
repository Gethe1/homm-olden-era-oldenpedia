// The C# compiler can require these attribute types to exist in order to emit
// nullable-annotation metadata (e.g. for value tuples, or when matching against
// an already-nullable-annotated API in a referenced assembly), independently of
// this project's own <Nullable>disable</Nullable> setting. This game's reference
// assemblies don't ship them (unlike a normal modern .NET SDK), which surfaces as:
//   error CS0656: Missing compiler required member
//   'System.Runtime.CompilerServices.NullableAttribute..ctor'
// Defining them ourselves (any accessible type with the right name/shape) gives
// the compiler something to bind to. This is the standard, well-known workaround
// for this exact error in Unity/IL2CPP modding and other non-standard targets.
namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.All, AllowMultiple = false, Inherited = false)]
    internal sealed class NullableAttribute : Attribute
    {
        public readonly byte[] NullableFlags;
        public NullableAttribute(byte b) { NullableFlags = new[] { b }; }
        public NullableAttribute(byte[] b) { NullableFlags = b; }
    }

    [AttributeUsage(AttributeTargets.Module | AttributeTargets.Class | AttributeTargets.Struct |
                     AttributeTargets.Interface | AttributeTargets.Constructor | AttributeTargets.Method |
                     AttributeTargets.Property | AttributeTargets.Event | AttributeTargets.Field,
                     AllowMultiple = false, Inherited = false)]
    internal sealed class NullableContextAttribute : Attribute
    {
        public readonly byte Flag;
        public NullableContextAttribute(byte b) { Flag = b; }
    }

    [AttributeUsage(AttributeTargets.Module, AllowMultiple = false, Inherited = false)]
    internal sealed class NullablePublicOnlyAttribute : Attribute
    {
        public readonly bool IncludesInternals;
        public NullablePublicOnlyAttribute(bool b) { IncludesInternals = b; }
    }
}
