using System.Collections.Generic;

namespace UrbanoMetraj.BoQ.ExcelImport
{
    /// <summary>User's answer to "which Excel column feeds which catalog field", plus any
    /// per-value mappings (raw cell text -> target enum Value) for enum-like fields.</summary>
    public sealed class ColumnMappingResult
    {
        // fieldKey -> column index in the sheet (-1 = not mapped / "(Kullanma)")
        public Dictionary<string, int> ColumnIndex { get; } = new Dictionary<string, int>();

        // fieldKey -> (raw cell text -> target enum Value); only present for IsEnum fields
        public Dictionary<string, Dictionary<string, string>> ValueMaps { get; }
            = new Dictionary<string, Dictionary<string, string>>();

        public int GetColumn(string key)
        {
            int idx;
            return ColumnIndex.TryGetValue(key, out idx) ? idx : -1;
        }

        /// <summary>Returns the mapped target value for a raw cell value, or null if unmapped/unknown.</summary>
        public string MapValue(string key, string raw)
        {
            if (raw == null) return null;
            Dictionary<string, string> map;
            string target;
            if (ValueMaps.TryGetValue(key, out map) && map.TryGetValue(raw.Trim(), out target))
                return target;
            return null;
        }
    }
}
