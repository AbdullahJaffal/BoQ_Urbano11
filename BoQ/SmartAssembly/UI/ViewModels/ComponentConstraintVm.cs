using UrbanoMetraj.BoQ.SmartAssembly.Models;

namespace UrbanoMetraj.BoQ.SmartAssembly.UI.ViewModels
{
    /// <summary>
    /// Row ViewModel for a single ComponentTypeConstraint in the depth-tier constraint panel.
    /// IsReadOnly = true for Taban (always 1/1, locked).
    /// IsZeroOrOne = true for Konik and Rögar Kapağı (only 0 or 1 allowed).
    /// MaxCount = -1 means unlimited (∞) — system uses as many as needed to reach target height.
    /// MaxCount =  0 means don't use any piece of this type.
    /// MaxCount =  N means use at most N pieces.
    /// </summary>
    public class ComponentConstraintVm : ViewModelBase
    {
        private readonly ComponentTypeConstraint _model;

        public ComponentConstraintVm(ComponentTypeConstraint model, bool isReadOnly, bool isZeroOrOne)
        {
            _model      = model;
            IsReadOnly  = isReadOnly;
            IsZeroOrOne = isZeroOrOne;
            RoleDisplay = RoleToDisplay(model.Role);
        }

        public string RoleDisplay  { get; }
        public bool   IsReadOnly   { get; }
        public bool   IsZeroOrOne  { get; }

        public bool   IsEditableFree      => !IsReadOnly && !IsZeroOrOne;
        public bool   IsEditableZeroOrOne => !IsReadOnly && IsZeroOrOne;

        /// <summary>Items for the ComboBox on ZeroOrOne rows (Konik, Rögar Kapağı).</summary>
        public int[] ZeroOneOptions { get; } = new[] { 0, 1 };

        public int MinCount
        {
            get => _model.MinCount;
            set { _model.MinCount = value; OnPropertyChanged(); }
        }

        public int MaxCount
        {
            get => _model.MaxCount;
            set
            {
                _model.MaxCount = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(MaxDisplay));
                OnPropertyChanged(nameof(MaxCountText));
            }
        }

        /// <summary>Cell display: ∞ when MaxCount=-1 (unlimited), otherwise the number.</summary>
        public string MaxDisplay => _model.MaxCount == -1 ? "∞" : _model.MaxCount.ToString();

        /// <summary>
        /// String binding for the TextBox on free-count rows.
        /// Accepts an integer or "∞" (stores -1 for unlimited).
        /// </summary>
        public string MaxCountText
        {
            get => _model.MaxCount == -1 ? "∞" : _model.MaxCount.ToString();
            set
            {
                if (value != null && value.Trim() == "∞")
                {
                    _model.MaxCount = -1;
                }
                else
                {
                    int v;
                    if (int.TryParse(value?.Trim(), out v))
                        _model.MaxCount = v;
                }
                OnPropertyChanged(nameof(MaxCountText));
                OnPropertyChanged(nameof(MaxDisplay));
                OnPropertyChanged(nameof(MaxCount));
            }
        }

        private static string RoleToDisplay(ComponentRole role)
        {
            switch (role)
            {
                case ComponentRole.BottomElement: return "Taban";
                case ComponentRole.MiddleElement: return "Gövde Halkası";
                case ComponentRole.Reducer:       return "Konik";
                case ComponentRole.Adjuster:      return "Boyun bileziği";
                case ComponentRole.Cover:         return "Rögar Kapağı";
                default:                          return role.ToString();
            }
        }
    }
}
