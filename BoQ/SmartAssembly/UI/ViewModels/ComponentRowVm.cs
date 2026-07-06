using System;
using UrbanoMetraj.BoQ.SmartAssembly.Models;

namespace UrbanoMetraj.BoQ.SmartAssembly.UI.ViewModels
{
    /// <summary>
    /// Flat DataGrid row ViewModel for any <see cref="ManholeComponent"/>.
    /// The DataGrid shows only common columns; role-specific fields are
    /// exposed here for the right-side detail panel.
    /// </summary>
    public class ComponentRowVm : ViewModelBase
    {
        private readonly ManholeComponent _model;

        public ComponentRowVm(ManholeComponent model)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
        }

        public ManholeComponent Model => _model;

        // ── Common (DataGrid + detail panel) ──────────────────────────────────

        public Guid   Id              { get => _model.Id;              set { _model.Id              = value; OnPropertyChanged(); } }
        public string PozNo           { get => _model.PozNo;           set { _model.PozNo           = value; OnPropertyChanged(); } }
        public string Name            { get => _model.Name;            set { _model.Name            = value; OnPropertyChanged(); } }
        public string RoleDisplay
        {
            get
            {
                switch (_model.Role)
                {
                    case ComponentRole.BottomElement: return "Taban";
                    case ComponentRole.MiddleElement: return "Gövde Halkası";
                    case ComponentRole.Reducer:       return "Konik";
                    case ComponentRole.Adjuster:      return "Boyun bileziği";
                    case ComponentRole.Cover:         return "Rögar Kapağı";
                    default:                          return _model.Role.ToString();
                }
            }
        }
        public string FamilyTag  { get => _model.FamilyTag;  set { _model.FamilyTag  = value; OnPropertyChanged(); } }
        public string Aciklama   { get => _model.Aciklama;   set { _model.Aciklama   = value; OnPropertyChanged(); } }

        // ── değişken (variable height) ────────────────────────────────────────

        public bool IsVariable
        {
            get => _model.IsVariable;
            set
            {
                _model.IsVariable = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsEffectiveHeightReadOnly));
                OnPropertyChanged(nameof(IsCompositeEnabled));
                OnPropertyChanged(nameof(EffectiveHeight));
                OnPropertyChanged(nameof(TotalHeightMm));
            }
        }

        // ── zorunlu parça / yükseltme parçası ────────────────────────────────

        public bool ZorunluParca
        {
            get => _model.ZorunluParca;
            set { _model.ZorunluParca = value; OnPropertyChanged(); }
        }

        public bool YukseltmeParcasi
        {
            get => _model.YukseltmeParcasi;
            set { _model.YukseltmeParcasi = value; OnPropertyChanged(); }
        }

        /// <summary>True for all types except BottomElement — controls visibility of "Zorunlu Parça" checkbox.</summary>
        public bool ShowZorunluParca     => !(_model is BottomElementComponent);

        /// <summary>True only for MiddleElement and Adjuster — controls visibility of "Yükseltme Parçası" checkbox.</summary>
        public bool ShowYukseltmeParcasi => _model is MiddleElementComponent || _model is AdjusterComponent;

        /// <summary>
        /// When IsComposite (and not IsVariable): auto-calculated as sum of SubPiece heights (read-only).
        /// When IsVariable: read-only, value is kept manually but cannot be edited in UI.
        /// Otherwise: manually entered shaft height.
        /// </summary>
        public double EffectiveHeight
        {
            get
            {
                var b = _model as BottomElementComponent;
                if (b != null && b.IsComposite && !_model.IsVariable)
                {
                    double sum = 0;
                    foreach (var sp in b.SubPieces) sum += sp.HeightMm;
                    return sum;
                }
                return _model.EffectiveHeight;
            }
            set
            {
                if (_model.IsVariable) return;
                var b = _model as BottomElementComponent;
                if (b != null && b.IsComposite) return;
                _model.EffectiveHeight = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(TotalHeightMm));
            }
        }

        /// <summary>True when EffectiveHeight cannot be edited: either IsVariable or IsComposite (BottomElement only).</summary>
        public bool IsEffectiveHeightReadOnly
        {
            get
            {
                if (_model.IsVariable) return true;
                var b = _model as BottomElementComponent;
                return b != null && b.IsComposite;
            }
        }

        /// <summary>
        /// The value shown in the main DataGrid "Yükseklik" column.
        /// For BottomElement: EffectiveHeight (shaft) + TabanKalinligiMm (slab).
        /// For all other roles: EffectiveHeight only.
        /// </summary>
        public double TotalHeightMm
        {
            get
            {
                var b = _model as BottomElementComponent;
                return b != null ? EffectiveHeight + b.TabanKalinligiMm : _model.EffectiveHeight;
            }
        }

        /// <summary>
        /// Called by RepositoryTabVm when a SubPiece height changes.
        /// Syncs the model's EffectiveHeight and fires notifications.
        /// No-ops when IsVariable (variable-height mode overrides composite calculation).
        /// </summary>
        public void RecalcEffectiveHeight()
        {
            var b = _model as BottomElementComponent;
            if (b == null || !b.IsComposite || _model.IsVariable) return;
            double sum = 0;
            foreach (var sp in b.SubPieces) sum += sp.HeightMm;
            b.EffectiveHeight = sum;
            OnPropertyChanged(nameof(EffectiveHeight));
            OnPropertyChanged(nameof(TotalHeightMm));
        }

        // ── Common volume properties (base class) ─────────────────────────────

        public double ExternalVolume
        {
            get => _model.ExternalVolume;
            set { _model.ExternalVolume = value; OnPropertyChanged(); }
        }

        public double MaterialVolume
        {
            get => _model.MaterialVolume;
            set { _model.MaterialVolume = value; OnPropertyChanged(); }
        }

        // ── Wall thickness (BottomElement / MiddleElement / Reducer / Adjuster) ─

        public double? WallThicknessMm
        {
            get
            {
                var b = _model as BottomElementComponent;  if (b != null) return b.WallThicknessMm;
                var m = _model as MiddleElementComponent;  if (m != null) return m.WallThicknessMm;
                var r = _model as ReducerComponent;        if (r != null) return r.WallThicknessMm;
                var a = _model as AdjusterComponent;       if (a != null) return a.WallThicknessMm;
                return null;
            }
            set
            {
                if (!value.HasValue) return;
                var b = _model as BottomElementComponent;  if (b != null) { b.WallThicknessMm = value.Value; OnPropertyChanged(); return; }
                var m = _model as MiddleElementComponent;  if (m != null) { m.WallThicknessMm = value.Value; OnPropertyChanged(); return; }
                var r = _model as ReducerComponent;        if (r != null) { r.WallThicknessMm = value.Value; OnPropertyChanged(); return; }
                var a = _model as AdjusterComponent;       if (a != null) { a.WallThicknessMm = value.Value; OnPropertyChanged(); }
            }
        }

        // ── BottomElement — top opening (shaft link diameter) ──────────────────

        public double? TopOpeningDiamMm
        {
            get { var b = _model as BottomElementComponent; return b != null ? (double?)b.TopOpeningDiameterMm : null; }
            set { var b = _model as BottomElementComponent; if (b != null && value.HasValue) { b.TopOpeningDiameterMm = value.Value; OnPropertyChanged(); } }
        }

        // ── BottomElement — footprint (excavation plan) ────────────────────────

        public FootprintShape PlanShape
        {
            get { var b = _model as BottomElementComponent; return b?.Footprint?.Shape ?? FootprintShape.Circular; }
            set
            {
                var b = _model as BottomElementComponent;
                if (b?.Footprint == null) return;
                b.Footprint.Shape = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsCircularFootprint));
                OnPropertyChanged(nameof(IsSquareFootprint));
                OnPropertyChanged(nameof(IsRectangularFootprint));
            }
        }

        public bool IsCircularFootprint
            => (_model as BottomElementComponent)?.Footprint?.Shape == FootprintShape.Circular;

        public bool IsSquareFootprint
            => (_model as BottomElementComponent)?.Footprint?.Shape == FootprintShape.Square;

        public bool IsRectangularFootprint
            => (_model as BottomElementComponent)?.Footprint?.Shape == FootprintShape.Rectangular;

        public double? FootprintDiamMm
        {
            get { var b = _model as BottomElementComponent; return b?.Footprint != null ? (double?)b.Footprint.DiameterMm : null; }
            set { var b = _model as BottomElementComponent; if (b?.Footprint != null && value.HasValue) { b.Footprint.DiameterMm = value.Value; OnPropertyChanged(); } }
        }

        public double? FootprintSideMm
        {
            get { var b = _model as BottomElementComponent; return b?.Footprint != null ? (double?)b.Footprint.SideMm : null; }
            set { var b = _model as BottomElementComponent; if (b?.Footprint != null && value.HasValue) { b.Footprint.SideMm = value.Value; OnPropertyChanged(); } }
        }

        public double? FootprintLengthMm
        {
            get { var b = _model as BottomElementComponent; return b?.Footprint != null ? (double?)b.Footprint.LengthMm : null; }
            set { var b = _model as BottomElementComponent; if (b?.Footprint != null && value.HasValue) { b.Footprint.LengthMm = value.Value; OnPropertyChanged(); } }
        }

        public double? FootprintWidthMm
        {
            get { var b = _model as BottomElementComponent; return b?.Footprint != null ? (double?)b.Footprint.WidthMm : null; }
            set { var b = _model as BottomElementComponent; if (b?.Footprint != null && value.HasValue) { b.Footprint.WidthMm = value.Value; OnPropertyChanged(); } }
        }

        public string FootprintOrientation
        {
            get { var b = _model as BottomElementComponent; return b?.Footprint?.RectangularOrientation ?? ""; }
            set { var b = _model as BottomElementComponent; if (b?.Footprint != null) { b.Footprint.RectangularOrientation = value; OnPropertyChanged(); } }
        }

        // ── BottomElement — composite sub-pieces ──────────────────────────────

        /// <summary>False when IsVariable — the composite checkbox is disabled when height is marked variable.</summary>
        public bool IsCompositeEnabled => !_model.IsVariable;

        public bool IsCompositeChecked
        {
            get { var b = _model as BottomElementComponent; return b?.IsComposite ?? false; }
            set
            {
                var b = _model as BottomElementComponent;
                if (b == null || _model.IsVariable) return;
                b.IsComposite = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsEffectiveHeightReadOnly));
                OnPropertyChanged(nameof(EffectiveHeight));
                OnPropertyChanged(nameof(TotalHeightMm));
            }
        }

        // ── BottomElement — base slab thickness ───────────────────────────────

        public double? TabanKalinligiMm
        {
            get { var b = _model as BottomElementComponent; return b != null ? (double?)b.TabanKalinligiMm : null; }
            set
            {
                var b = _model as BottomElementComponent;
                if (b != null && value.HasValue)
                {
                    b.TabanKalinligiMm = value.Value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(TotalHeightMm));
                }
            }
        }

        // ── BottomElement — temel altı parça ─────────────────────────────────

        public bool TemelAltiParcaEnabled
        {
            get { var b = _model as BottomElementComponent; return b?.TemelAltiParcaEnabled ?? false; }
            set
            {
                var b = _model as BottomElementComponent;
                if (b == null) return;
                b.TemelAltiParcaEnabled = value;
                OnPropertyChanged();
            }
        }

        // ── MiddleElement / Adjuster ──────────────────────────────────────────

        public double? InnerDiamMm
        {
            get
            {
                var m = _model as MiddleElementComponent; if (m != null) return m.InnerDiameterMm;
                var a = _model as AdjusterComponent;      if (a != null) return a.InnerDiameterMm;
                return null;
            }
            set
            {
                if (!value.HasValue) return;
                var m = _model as MiddleElementComponent; if (m != null) { m.InnerDiameterMm = value.Value; OnPropertyChanged(); return; }
                var a = _model as AdjusterComponent;      if (a != null) { a.InnerDiameterMm = value.Value; OnPropertyChanged(); }
            }
        }

        // ── Reducer ───────────────────────────────────────────────────────────

        public double? BottomDiamMm
        {
            get { var r = _model as ReducerComponent; return r != null ? (double?)r.BottomInnerDiameterMm : null; }
            set { var r = _model as ReducerComponent; if (r != null && value.HasValue) { r.BottomInnerDiameterMm = value.Value; OnPropertyChanged(); } }
        }

        public double? TopDiamMm
        {
            get { var r = _model as ReducerComponent; return r != null ? (double?)r.TopInnerDiameterMm : null; }
            set { var r = _model as ReducerComponent; if (r != null && value.HasValue) { r.TopInnerDiameterMm = value.Value; OnPropertyChanged(); } }
        }

        // ── Cover ─────────────────────────────────────────────────────────────

        public string LoadClass
        {
            get { var cv = _model as CoverComponent; return cv?.LoadClass; }
            set { var cv = _model as CoverComponent; if (cv != null) { cv.LoadClass = value; OnPropertyChanged(); } }
        }

        public double? ClearOpeningMm
        {
            get { var cv = _model as CoverComponent; return cv != null ? (double?)cv.ClearOpeningMm : null; }
            set { var cv = _model as CoverComponent; if (cv != null && value.HasValue) { cv.ClearOpeningMm = value.Value; OnPropertyChanged(); } }
        }
    }
}
