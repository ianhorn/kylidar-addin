using System;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace KylidarAddin
{
    public partial class BufferInputDialog : Window
    {
        public double BufferFeet { get; private set; }
        public string OutputLasPath { get; private set; }

        public BufferInputDialog()
        {
            InitializeComponent();
            OutputPathBox.Text = Path.Combine(Path.GetTempPath(), $"kylidar_clip_{DateTime.Now:yyyyMMdd_HHmmss}.las");
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                Filter = "LAS files (*.las)|*.las",
                FileName = Path.GetFileName(OutputPathBox.Text)
            };
            if (dlg.ShowDialog() == true)
                OutputPathBox.Text = dlg.FileName;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (!double.TryParse(BufferFeetBox.Text, out var feet) || feet < 0)
            {
                ValidationText.Text = "Enter a buffer distance of 0 or more.";
                return;
            }
            if (string.IsNullOrWhiteSpace(OutputPathBox.Text))
            {
                ValidationText.Text = "Choose an output .las file path.";
                return;
            }

            BufferFeet = feet;
            OutputLasPath = OutputPathBox.Text;
            DialogResult = true;
        }
    }
}
