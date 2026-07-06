using System;
using System.Collections.Generic;
using System.Globalization;
using UrbanoMetraj.BoQ.ExcelImport;
using UrbanoMetraj.BoQ.SmartAssembly.Models;

namespace UrbanoMetraj.BoQ.SmartAssembly.Services
{
    /// <summary>
    /// Builds ManholeComponent rows from a user-mapped Excel sheet (Baca Parça Kataloğu bulk import).
    ///
    /// V1 scope: only fields common to every ComponentRole are imported. Role-specific
    /// geometry (BottomElement's Footprint/composite SubPieces, ring inner diameters, ...)
    /// is left at defaults and must still be filled in manually — those fields vary per
    /// role and are comparatively low-volume (one Taban row per family) versus the
    /// repetitive ring/cone rows this import is meant to speed up.
    /// </summary>
    public static class ComponentExcelImportService
    {
        public const string FieldPozNo      = "PozNo";
        public const string FieldName       = "Name";
        public const string FieldRole       = "Role";
        public const string FieldHeight     = "Height";
        public const string FieldFamilyTag  = "FamilyTag";
        public const string FieldExtVolume  = "ExtVolume";
        public const string FieldMatVolume  = "MatVolume";
        public const string FieldAciklama   = "Aciklama";
        public const string FieldIsVariable = "IsVariable";
        public const string FieldZorunlu    = "Zorunlu";
        public const string FieldYukseltme  = "Yukseltme";

        public static readonly EnumOption[] RoleOptions =
        {
            new EnumOption(ComponentRole.BottomElement.ToString(), "Taban"),
            new EnumOption(ComponentRole.MiddleElement.ToString(), "Gövde Halkası"),
            new EnumOption(ComponentRole.Reducer.ToString(),       "Konik"),
            new EnumOption(ComponentRole.Adjuster.ToString(),      "Boyun bileziği"),
            new EnumOption(ComponentRole.Cover.ToString(),         "Rögar Kapağı"),
        };

        public static MappableField[] Fields => new[]
        {
            new MappableField(FieldPozNo,      "Poz No"),
            new MappableField(FieldName,       "Ad"),
            new MappableField(FieldRole,       "Rol", isEnum: true, enumOptions: RoleOptions),
            new MappableField(FieldHeight,     "Yükseklik (mm)"),
            new MappableField(FieldFamilyTag,  "Aile Etiketi"),
            new MappableField(FieldExtVolume,  "Dış Hacim (m³)"),
            new MappableField(FieldMatVolume,  "Malzeme Hacmi (m³)"),
            new MappableField(FieldAciklama,   "Açıklama"),
            new MappableField(FieldIsVariable, "Değişken (Evet/Hayır)"),
            new MappableField(FieldZorunlu,    "Zorunlu Parça (Evet/Hayır)"),
            new MappableField(FieldYukseltme,  "Yükseltme Parçası (Evet/Hayır)"),
        };

        /// <summary>
        /// fallbackRole is used for every row when the Rol column is left unmapped, or
        /// when a specific row's raw text has no entry in the value map — a row is never
        /// silently dropped for an unrecognized Rol value, it falls back instead.
        /// </summary>
        public static List<ManholeComponent> Build(List<string[]> rows, ColumnMappingResult map,
                                                    ComponentRole fallbackRole)
        {
            var result = new List<ManholeComponent>();
            foreach (var row in rows)
            {
                ComponentRole role = fallbackRole;
                string rawRole = GetString(row, map, FieldRole);
                if (!string.IsNullOrEmpty(rawRole))
                {
                    string mapped = map.MapValue(FieldRole, rawRole);
                    ComponentRole parsed;
                    if (mapped != null && Enum.TryParse(mapped, out parsed))
                        role = parsed;
                }

                ManholeComponent comp = CreateForRole(role);

                comp.PozNo            = GetString(row, map, FieldPozNo);
                comp.Name             = GetString(row, map, FieldName);
                comp.EffectiveHeight  = GetDouble(row, map, FieldHeight);
                comp.FamilyTag        = GetString(row, map, FieldFamilyTag);
                comp.ExternalVolume   = GetDouble(row, map, FieldExtVolume);
                comp.MaterialVolume   = GetDouble(row, map, FieldMatVolume);
                comp.Aciklama         = GetString(row, map, FieldAciklama);
                comp.IsVariable       = GetBool(row, map, FieldIsVariable);
                comp.ZorunluParca     = GetBool(row, map, FieldZorunlu);
                comp.YukseltmeParcasi = GetBool(row, map, FieldYukseltme);

                bool empty = string.IsNullOrEmpty(comp.PozNo) && string.IsNullOrEmpty(comp.Name)
                          && comp.EffectiveHeight <= 0;
                if (!empty) result.Add(comp);
            }
            return result;
        }

        private static ManholeComponent CreateForRole(ComponentRole role)
        {
            switch (role)
            {
                case ComponentRole.BottomElement: return new BottomElementComponent();
                case ComponentRole.Reducer:        return new ReducerComponent();
                case ComponentRole.Adjuster:        return new AdjusterComponent();
                case ComponentRole.Cover:            return new CoverComponent();
                default:                             return new MiddleElementComponent();
            }
        }

        private static string GetString(string[] row, ColumnMappingResult map, string key)
        {
            int idx = map.GetColumn(key);
            if (idx < 0 || idx >= row.Length) return "";
            return row[idx] ?? "";
        }

        private static double GetDouble(string[] row, ColumnMappingResult map, string key)
        {
            string s = GetString(row, map, key);
            if (string.IsNullOrWhiteSpace(s)) return 0;
            s = s.Replace(",", ".");
            double v;
            return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        private static bool GetBool(string[] row, ColumnMappingResult map, string key)
        {
            string s = GetString(row, map, key).Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(s)) return false;
            return s == "evet" || s == "true" || s == "1" || s == "yes" || s == "x" || s == "var";
        }
    }
}
