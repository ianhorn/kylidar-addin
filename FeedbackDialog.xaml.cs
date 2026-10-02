/*
 * Code-behind for the Feedback dialog -- see FeedbackDialog.xaml's header comment for the
 * two-panel layout. "Report an Issue on GitHub" opens the browser and closes immediately; "Send
 * Feedback Directly" swaps in the message form in place and posts via FeedbackService on Send.
 */
using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using KylidarAddin.Services;

namespace KylidarAddin
{
    public partial class FeedbackDialog : Window
    {
        private static readonly SolidColorBrush ErrorBrush = Brushes.Firebrick;
        private static readonly SolidColorBrush NeutralBrush = new(Color.FromRgb(0x55, 0x55, 0x55));
        private static readonly SolidColorBrush SuccessBrush = new(Color.FromRgb(0x1E, 0x7E, 0x34));

        private CancellationTokenSource _sendCts;

        public FeedbackDialog()
        {
            InitializeComponent();
        }

        private void GitHubButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(FeedbackService.BuildGitHubIssueUrl()) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Couldn't open the browser: {ex.Message}\n\nYou can file an issue directly at {FeedbackService.RepoUrl}/issues.",
                    "Kylidar", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Close();
        }

        private void DirectButton_Click(object sender, RoutedEventArgs e)
        {
            ChoicePanel.Visibility = Visibility.Collapsed;
            DirectPanel.Visibility = Visibility.Visible;
            BackButton.Visibility = Visibility.Visible;
            SendButton.Visibility = Visibility.Visible;
            MessageTextBox.Focus();
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            _sendCts?.Cancel();
            DirectPanel.Visibility = Visibility.Collapsed;
            ChoicePanel.Visibility = Visibility.Visible;
            BackButton.Visibility = Visibility.Collapsed;
            SendButton.Visibility = Visibility.Collapsed;
            StatusText.Visibility = Visibility.Collapsed;
        }

        private async void Send_Click(object sender, RoutedEventArgs e)
        {
            var message = MessageTextBox.Text?.Trim();
            if (string.IsNullOrEmpty(message))
            {
                ShowStatus("Enter a message first.", ErrorBrush);
                return;
            }

            SetSending(true);
            ShowStatus("Sending...", NeutralBrush);
            _sendCts = new CancellationTokenSource();
            var (ok, error) = await FeedbackService.SendDirectAsync(message, EmailBox.Text, _sendCts.Token);

            if (ok)
            {
                DirectPanel.Visibility = Visibility.Collapsed;
                BackButton.Visibility = Visibility.Collapsed;
                SendButton.Visibility = Visibility.Collapsed;
                ShowStatus("Thanks -- your feedback was sent.", SuccessBrush);
            }
            else
            {
                SetSending(false);
                ShowStatus("Couldn't send that: " + error, ErrorBrush);
            }
        }

        private void SetSending(bool sending)
        {
            SendButton.IsEnabled = !sending;
            BackButton.IsEnabled = !sending;
            MessageTextBox.IsEnabled = !sending;
            EmailBox.IsEnabled = !sending;
        }

        private void ShowStatus(string text, SolidColorBrush color)
        {
            StatusText.Text = text;
            StatusText.Foreground = color;
            StatusText.Visibility = Visibility.Visible;
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            _sendCts?.Cancel();
            Close();
        }
    }
}
