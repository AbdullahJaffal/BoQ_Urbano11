using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace UrbanoMetraj.BoQ.PipeCatalogs.Models
{
    public class PipeDefinition : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private void Notify([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public Guid Id { get; set; } = Guid.NewGuid();

        private string _pozNo = "";
        public string PozNo
        {
            get => _pozNo;
            set { if (_pozNo == value) return; _pozNo = value; Notify(); }
        }

        private double _nominalDiameter;
        public double NominalDiameter
        {
            get => _nominalDiameter;
            set { if (_nominalDiameter == value) return; _nominalDiameter = value; Notify(); }
        }

        private double _outerDiameter;
        public double OuterDiameter
        {
            get => _outerDiameter;
            set
            {
                if (_outerDiameter == value) return;
                _outerDiameter = value;
                Notify();
                Notify(nameof(IsConsistent));
            }
        }

        private double _innerDiameter;
        public double InnerDiameter
        {
            get => _innerDiameter;
            set
            {
                if (_innerDiameter == value) return;
                _innerDiameter = value;
                Notify();
                Notify(nameof(IsConsistent));
            }
        }

        private double _wallThickness;
        public double WallThickness
        {
            get => _wallThickness;
            set
            {
                if (_wallThickness == value) return;
                _wallThickness = value;
                Notify();
                Notify(nameof(IsConsistent));
            }
        }

        private string _aciklama = "";
        public string Aciklama
        {
            get => _aciklama;
            set { if (_aciklama == value) return; _aciklama = value; Notify(); }
        }

        // Used by SmartAssembly ComboBox display.
        public string Display =>
            string.Format("DN{0:0}  (OD {1:0.0} / ID {2:0.0} mm)",
                          _nominalDiameter, _outerDiameter, _innerDiameter);

        // True when values are geometrically consistent, or when at least one
        // dimension is still zero (not yet entered — Normalize() will fill it later).
        public bool IsConsistent
        {
            get
            {
                if (_outerDiameter <= 0 || _innerDiameter <= 0 || _wallThickness <= 0)
                    return true;
                return Math.Abs(_outerDiameter - (_innerDiameter + 2.0 * _wallThickness)) < 0.01;
            }
        }

        // Called when the DataGrid row edit ends.
        // If exactly one of OD / ID / WT is missing (zero), derives it from the other two.
        public void Normalize()
        {
            bool hasOD = _outerDiameter > 0;
            bool hasID = _innerDiameter > 0;
            bool hasWT = _wallThickness > 0;

            if (!hasOD && hasID && hasWT)
            {
                _outerDiameter = _innerDiameter + 2.0 * _wallThickness;
                Notify(nameof(OuterDiameter));
            }
            else if (!hasID && hasOD && hasWT)
            {
                _innerDiameter = _outerDiameter - 2.0 * _wallThickness;
                Notify(nameof(InnerDiameter));
            }
            else if (!hasWT && hasOD && hasID)
            {
                _wallThickness = (_outerDiameter - _innerDiameter) / 2.0;
                Notify(nameof(WallThickness));
            }

            Notify(nameof(IsConsistent));
        }
    }

    public class PipeFamily : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        private void Notify([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public Guid Id { get; set; } = Guid.NewGuid();

        private string _familyName = "";
        public string FamilyName
        {
            get => _familyName;
            set { if (_familyName == value) return; _familyName = value; Notify(); }
        }

        private string _material = "";
        public string Material
        {
            get => _material;
            set { if (_material == value) return; _material = value; Notify(); }
        }

        public ObservableCollection<PipeDefinition> Pipes { get; set; }
            = new ObservableCollection<PipeDefinition>();

        public override string ToString() => FamilyName;
    }

    public class PipeCatalog
    {
        public ObservableCollection<PipeFamily> Families { get; set; }
            = new ObservableCollection<PipeFamily>();

        public DateTime LastModified { get; set; } = DateTime.Now;
    }
}
