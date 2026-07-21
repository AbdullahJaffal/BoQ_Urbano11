using System;
using System.Reflection;

namespace UrbanoMetraj.BoQ.UI.NetworkPanel
{
    /// <summary>
    /// Detects whether UrbanoLock's assembly is already loaded in the current
    /// AutoCAD session and, if so, reflectively drives its own network palette
    /// (UrbanoLock.UI.NetworkPaletteSet) instead of letting UrbanoMetraj create
    /// a second, independent window.
    ///
    /// UrbanoLock and UrbanoMetraj are separately built/deployed plugin DLLs with
    /// no project reference between them, so this goes through reflection rather
    /// than a hard dependency. When UrbanoLock isn't installed or hasn't been
    /// netloaded yet, <see cref="TryShowExisting"/> simply returns false and the
    /// caller falls back to UrbanoMetraj's own <see cref="NetworkPaletteSet"/>.
    /// </summary>
    internal static class UrbanoLockBridge
    {
        private const string AssemblyName = "UrbanoLock";
        private const string TypeName     = "UrbanoLock.UI.NetworkPaletteSet";

        /// <summary>
        /// If UrbanoLock's NetworkPaletteSet is loaded, calls its EnsureVisible()
        /// and RefreshFromCurrentDocument() statics and returns true. Returns false
        /// (no-op) if UrbanoLock isn't loaded or the call fails for any reason.
        /// </summary>
        internal static bool TryShowExisting()
        {
            var type = FindType();
            if (type == null) return false;

            try
            {
                InvokeStatic(type, "EnsureVisible");
                InvokeStatic(type, "RefreshFromCurrentDocument");
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void InvokeStatic(Type type, string methodName)
        {
            var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
            method?.Invoke(null, null);
        }

        private static Type FindType()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!string.Equals(asm.GetName().Name, AssemblyName, StringComparison.OrdinalIgnoreCase))
                    continue;

                Type type;
                try { type = asm.GetType(TypeName, throwOnError: false); }
                catch { continue; }

                if (type != null) return type;
            }
            return null;
        }
    }
}
