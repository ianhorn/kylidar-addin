using ArcGIS.Desktop.Framework.Contracts;

namespace KylidarAddin
{
    /// <summary>One checkbox in the pane's Point filter: a LAS classification code and whether to keep it.</summary>
    internal class PointClassOption : PropertyChangedBase
    {
        private readonly System.Action _onChanged;
        private bool _isChecked = true;

        public PointClassOption(int code, string label, System.Action onChanged)
        {
            Code = code;
            Label = label;
            _onChanged = onChanged;
        }

        public int Code { get; }

        /// <summary>"2 Ground"; the catch-all keeps its own wording ("Other (19 and up)").</summary>
        public string Label { get; }

        public string DisplayLabel => Code < Services.PointFilter.OtherClass ? $"{Code} {Label}" : Label;

        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                if (SetProperty(ref _isChecked, value)) _onChanged?.Invoke();
            }
        }

        /// <summary>Set without re-running the change callback (presets set many boxes at once).</summary>
        public void SetSilently(bool value)
        {
            _isChecked = value;
            NotifyPropertyChanged(nameof(IsChecked));
        }
    }
}

namespace KylidarAddin
{
    /// <summary>A preset button ("All", "None", "Ground only", ...). A real class with properties, not a
    /// value tuple: WPF bindings only see properties, and a tuple's Name/Codes are fields, so a
    /// tuple-bound button renders blank and its command parameter is null.</summary>
    internal sealed class PointClassPreset
    {
        public PointClassPreset(string name, System.Collections.Generic.IReadOnlyCollection<int> codes)
        {
            Name = name;
            Codes = codes;
        }

        public string Name { get; }
        public System.Collections.Generic.IReadOnlyCollection<int> Codes { get; }
    }
}

namespace KylidarAddin
{
    /// <summary>A preset button for the return checkboxes ("Last (incl. single)", ...); a class with
    /// properties for the same reason as PointClassPreset.</summary>
    internal sealed class ReturnPreset
    {
        public ReturnPreset(string name, Services.ReturnGroups groups)
        {
            Name = name;
            Groups = groups;
        }

        public string Name { get; }
        public Services.ReturnGroups Groups { get; }
    }
}
