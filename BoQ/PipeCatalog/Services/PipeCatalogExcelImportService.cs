using System.Collections.Generic;
using System.Globalization;
using UrbanoMetraj.BoQ.ExcelImport;
using UrbanoMetraj.BoQ.PipeCatalogs.Models;

namespace UrbanoMetraj.BoQ.PipeCatalogs.Services
{
    /// <summary>Builds PipeDefinition rows from a user-mapped Excel sheet (Boru Kataloğu bulk import).</summary>
    public static class PipeCatalogExcelImportService
    {
        public const string FieldPozNo    = "PozNo";
        public const string FieldDN       = "DN";
        public const string FieldOD       = "OD";
        public const string FieldID       = "ID";
        public const string FieldWT       = "WT";
        public const string FieldSinif    = "Sinif";
        public const string FieldAciklama = "Aciklama";

        public static MappableField[] Fields => new[]
        {
            new MappableField(FieldPozNo,    "Poz No"),
            new MappableField(FieldDN,       "DN (mm)"),
            new MappableField(FieldOD,       "OD (mm)"),
            new MappableField(FieldID,       "ID (mm)"),
            new MappableField(FieldWT,       "Et Kalınlığı (mm)"),
            new MappableField(FieldSinif,    "Sınıf"),
            new MappableField(FieldAciklama, "Açıklama"),
        };

        public static List<PipeDefinition> Build(List<string[]> rows, ColumnMappingResult map)
        {
            var result = new List<PipeDefinition>();
            foreach (var row in rows)
            {
                var pipe = new PipeDefinition
                {
                    PozNo           = GetString(row, map, FieldPozNo),
                    NominalDiameter = GetDouble(row, map, FieldDN),
                    OuterDiameter   = GetDouble(row, map, FieldOD),
                    InnerDiameter   = GetDouble(row, map, FieldID),
                    WallThickness   = GetDouble(row, map, FieldWT),
                    Sinif           = GetString(row, map, FieldSinif),
                    Aciklama        = GetString(row, map, FieldAciklama)
                };
                pipe.Normalize();

                bool empty = string.IsNullOrEmpty(pipe.PozNo) && pipe.NominalDiameter <= 0
                          && pipe.OuterDiameter <= 0 && pipe.InnerDiameter <= 0 && pipe.WallThickness <= 0;
                if (!empty) result.Add(pipe);
            }
            return result;
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
    }
}
