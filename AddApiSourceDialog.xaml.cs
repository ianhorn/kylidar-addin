/*
 * Modal dialog for the "Bring Your Own API" feature: lets the user point the dock pane at
 * another STAC API, either alongside the built-in KyFromAbove catalog (Add) or in place
 * of the whole current source list (Replace all sources).
 *
 * Ported from kyfromabove-ext's AddApiSourceDialog.xaml.cs (same dialog, same logic).
 */
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KylidarAddin.Stac;

namespace KylidarAddin
{
    /// <summary>What the user chose to do with the entered API source, or that they cancelled.</summary>
    public enum AddApiSourceResult
    {
        Cancel,
        Add,
        Replace
    }

    public partial class AddApiSourceDialog : Window
    {
        private readonly CancellationTokenSource _cts = new();

        public AddApiSourceDialog()
        {
            InitializeComponent();
            if (ProTheme.IsDark) ApplyDarkPalette();
            NameBox.Focus();
            Loaded += async (_, _) => await LoadCatalogsAsync();
            Closed += (_, _) => _cts.Cancel();
        }

        /// <summary>Replace the catalog dropdown's light palette (defined in the XAML) with a dark one.</summary>
        private void ApplyDarkPalette()
        {
            static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
            Resources["CatalogComboBackground"] = Brush("#FF1E1E1E");
            Resources["CatalogComboForeground"] = Brush("#FFFFFFFF");
            Resources["CatalogComboBorder"] = Brush("#FF6A6A6E");
            Resources["CatalogComboHint"] = Brush("#FFB0B0B0");
            Resources["CatalogComboItemHighlight"] = Brush("#FF0078D7");
            Resources["CatalogComboItemSelected"] = Brush("#FF3F3F46");
        }

        /// <summary>Fill the dropdown from STAC Index. On failure the dropdown just stays disabled -- typing a URL still works.</summary>
        private async Task LoadCatalogsAsync()
        {
            CatalogCombo.IsEnabled = false;
            CatalogCombo.Tag = "Loading catalogs from STAC Index...";
            try
            {
                var catalogs = await StacIndexClient.GetSearchableCatalogsAsync(_cts.Token);
                CatalogCombo.ItemsSource = catalogs;
                CatalogCombo.IsEnabled = catalogs.Count > 0;
                CatalogCombo.Tag = catalogs.Count > 0 ? "Select a catalog..." : "No catalogs found";
            }
            catch (Exception) when (_cts.IsCancellationRequested)
            {
                // Dialog closed while loading.
            }
            catch (Exception)
            {
                // Offer just Kentucky's own catalog so there's still a way back to the default.
                CatalogCombo.ItemsSource = new[] { StacIndexClient.BuiltIn };
                CatalogCombo.IsEnabled = true;
                CatalogCombo.Tag = "Couldn't reach STAC Index -- only KyFromAbove is listed";
            }
        }

        private void CatalogCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CatalogCombo.SelectedItem is not StacIndexCatalog catalog) return;
            NameBox.Text = catalog.Name;
            UrlBox.Text = catalog.Url;
            ErrorText.Visibility = Visibility.Collapsed;
        }

        /// <summary>User-entered source name (falls back to the URL if left blank).</summary>
        public string SourceName => NameBox.Text?.Trim();

        /// <summary>User-entered STAC API base URL (no trailing slash).</summary>
        public string BaseUrl => UrlBox.Text?.Trim()?.TrimEnd('/');

        /// <summary>Which button the user clicked. Cancel unless Add/Replace was chosen.</summary>
        public AddApiSourceResult Result { get; private set; } = AddApiSourceResult.Cancel;

        private bool ValidateUrl()
        {
            var url = BaseUrl;
            if (string.IsNullOrWhiteSpace(url) ||
                !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                ErrorText.Text = "Enter a valid http(s):// URL for the STAC API base.";
                ErrorText.Visibility = Visibility.Visible;
                return false;
            }
            ErrorText.Visibility = Visibility.Collapsed;
            return true;
        }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            if (!ValidateUrl()) return;
            Result = AddApiSourceResult.Add;
            DialogResult = true;
        }

        private void Replace_Click(object sender, RoutedEventArgs e)
        {
            if (!ValidateUrl()) return;
            Result = AddApiSourceResult.Replace;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Result = AddApiSourceResult.Cancel;
            DialogResult = false;
        }
    }
}
