namespace UrbanoMetraj.BoQ.ExcelImport
{
    /// <summary>One target value + display label pair used by enum-like mapped fields (e.g. Rol).</summary>
    public sealed class EnumOption
    {
        public string Value   { get; }
        public string Display { get; }

        public EnumOption(string value, string display)
        {
            Value   = value;
            Display = display;
        }

        public override string ToString() => Display;
    }

    /// <summary>Describes one catalog field that a Excel column can be mapped to.</summary>
    public sealed class MappableField
    {
        public string Key   { get; }
        public string Label { get; }

        /// <summary>When true, ColumnMappingDialog also offers a per-value mapping step (raw text -> EnumOptions).</summary>
        public bool IsEnum { get; }
        public EnumOption[] EnumOptions { get; }

        public MappableField(string key, string label, bool isEnum = false, EnumOption[] enumOptions = null)
        {
            Key         = key;
            Label       = label;
            IsEnum      = isEnum;
            EnumOptions = enumOptions ?? new EnumOption[0];
        }
    }
}
