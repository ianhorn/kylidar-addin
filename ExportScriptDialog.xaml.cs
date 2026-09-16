/*
 * Modal dialog for "Export Script": lets the user pick the output script language (Python,
 * PowerShell, or shell) and the destination folder before KylidarDockpaneViewModel actually
 * writes the kit. Modeled directly on kyfromabove-ext's ExportScriptDialog -- no "Executable"
 * option here, since (unlike that add-in) this one has no bundled downloader .exe project to copy.
 */
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KylidarAddin.Services;

namespace KylidarAddin
{
    public partial class ExportScriptDialog : Window
    {
        private static readonly Dictionary<string, string> FormatHints = new()
        {
            ["Python"] = "Needs Python 3 on the machine that runs it.",
            ["Notebook"] = "Runs in Jupyter/JupyterLab, VS Code's notebook viewer, or Google Colab.",
            ["PowerShell"] = "Windows only, no install needed.",
            ["Shell"] = "macOS/Linux/WSL. Needs curl."
        };

        private static readonly SolidColorBrush SelectedBackground = new(Color.FromRgb(0x00, 0x78, 0xD7));
        private static readonly SolidColorBrush UnselectedBackground = Brushes.White;
        private static readonly SolidColorBrush UnselectedForeground = Brushes.Black;
        private static readonly SolidColorBrush UnselectedBorder = Brushes.Gray;

        private Button _selectedFormatButton;

        public ExportScriptDialog(string defaultDestinationFolder)
        {
            InitializeComponent();
            DestinationBox.Text = defaultDestinationFolder ?? "";
            _selectedFormatButton = PythonButton;
            UpdateFormatHint();
            DestinationBox.Focus();
            DestinationBox.CaretIndex = DestinationBox.Text.Length;
        }

        /// <summary>Which script language the user picked. Only meaningful when the dialog returns true.</summary>
        public ExportScriptFormat SelectedFormat
        {
            get
            {
                var tag = _selectedFormatButton?.Tag as string;
                return tag switch
                {
                    "Notebook" => ExportScriptFormat.Notebook,
                    "PowerShell" => ExportScriptFormat.PowerShell,
                    "Shell" => ExportScriptFormat.Shell,
                    _ => ExportScriptFormat.Python
                };
            }
        }

        /// <summary>Folder the downloads (and the generated script itself) will be written to.</summary>
        public string DestinationFolder => DestinationBox.Text?.Trim()?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        private void FormatButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button clicked) return;
            if (_selectedFormatButton != null)
            {
                _selectedFormatButton.Background = UnselectedBackground;
                _selectedFormatButton.Foreground = UnselectedForeground;
                _selectedFormatButton.BorderBrush = UnselectedBorder;
            }
            clicked.Background = SelectedBackground;
            clicked.Foreground = Brushes.White;
            clicked.BorderBrush = SelectedBackground;
            _selectedFormatButton = clicked;
            UpdateFormatHint();
        }

        private void UpdateFormatHint()
        {
            var tag = _selectedFormatButton?.Tag as string ?? "Python";
            FormatHintText.Text = FormatHints.TryGetValue(tag, out var hint) ? hint : "";
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select a folder for the downloaded/converted files and the generated script.",
                FolderName = string.IsNullOrWhiteSpace(DestinationFolder) ? Path.GetTempPath() : DestinationFolder
            };
            if (dlg.ShowDialog() == true) DestinationBox.Text = dlg.FolderName;
        }

        private bool ValidateDestination()
        {
            if (string.IsNullOrWhiteSpace(DestinationFolder))
            {
                ErrorText.Text = "Enter or browse for a destination folder.";
                ErrorText.Visibility = Visibility.Visible;
                return false;
            }
            ErrorText.Visibility = Visibility.Collapsed;
            return true;
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            if (!ValidateDestination()) return;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
