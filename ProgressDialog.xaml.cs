using System;
using System.Windows;
using System.Windows.Threading;

namespace KylidarAddin
{
    public partial class ProgressDialog : Window
    {
        public ProgressDialog(string title = "Progress")
        {
            InitializeComponent();
            Title = title;
            Prog.Visibility = Visibility.Visible;
            // The window "X" button raises Closing directly (it doesn't go through Close_Click),
            // so hook it here too -- callers only need to subscribe to CancelRequested once.
            Closing += (s, e) => RaiseCancelRequested();
        }

        /// <summary>Append a line of progress text (thread-safe). Close the window with the X button or Close button.</summary>
        public void Append(string msg)
        {
            if (Dispatcher == null) return;
            // Normal priority (not Background): Background-priority callbacks can be starved by
            // ongoing UI activity and may never run before the dialog is closed, leaving the log blank.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (LogBox == null) return;
                LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}");
                LogBox.ScrollToEnd();
            }), DispatcherPriority.Normal);
        }

        /// <summary>
        /// Close the dialog via the dispatcher queue (same priority as Append) so any messages
        /// appended just before this call are guaranteed to render first, instead of racing a
        /// direct/synchronous Close() call.
        /// </summary>
        public void CloseWhenReady()
        {
            if (Dispatcher == null) { return; }
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try { Close(); } catch { /* ignore if already closed by user */ }
            }), DispatcherPriority.Normal);
        }

        /// <summary>Raised when the user clicks Cancel, or closes the window, while the operation is still running.</summary>
        public event EventHandler CancelRequested;

        /// <summary>Call once the operation is finished (or can no longer be interrupted) to grey out the Cancel button.</summary>
        public void DisableCancel()
        {
            if (Dispatcher == null) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (CancelBtn != null) CancelBtn.IsEnabled = false;
            }), DispatcherPriority.Normal);
        }

        private bool _cancelRaised;
        private void RaiseCancelRequested()
        {
            // Skip if already raised, or if the operation already finished and disabled the
            // button itself -- otherwise CloseWhenReady()'s own Close() call (fired after a
            // successful run) would look like the user cancelling right after "Done."
            if (_cancelRaised || CancelBtn == null || !CancelBtn.IsEnabled) return;
            _cancelRaised = true;
            CancelBtn.IsEnabled = false;
            Append("Cancelling... (finishing the current step)");
            CancelRequested?.Invoke(this, EventArgs.Empty);
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => RaiseCancelRequested();

        // Close() raises the window's Closing event (wired in the constructor to RaiseCancelRequested),
        // so the "X" button and this button both end up cancelling consistently.
        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
