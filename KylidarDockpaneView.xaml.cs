using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace KylidarAddin
{
    public partial class KylidarDockpaneView : UserControl
    {
        // A plain string ToolTip renders as one unwrapped line, so the pane's long explanations ran off
        // the edge of the screen. Once loaded, every string tooltip in the pane is replaced with a real
        // ToolTip whose content wraps at a fixed width -- built in code rather than through a global
        // ToolTip style so it still picks up Pro's own tooltip theme instead of overriding it.
        private const double ToolTipWidth = 300;

        // The small "i" buttons (InfoButtonStyle) show that text on hover like any tooltip, and also on
        // click -- click keeps it open (dismissed by clicking the button again or anywhere else), which
        // is easier than holding the mouse still. The text is kept in the button's Tag once the ToolTip
        // property has been swapped for the wrapped ToolTip object above.
        private ToolTip _openInfo;

        public KylidarDockpaneView()
        {
            InitializeComponent();
            // Loaded can fire again if the pane is re-shown; the string check below makes that harmless.
            Loaded += (s, e) => WrapToolTips(this);
        }

        private void WrapToolTips(DependencyObject parent)
        {
            var infoStyle = Resources["InfoButtonStyle"] as Style;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is FrameworkElement element && element.ToolTip is string text)
                {
                    if (element is Button && ReferenceEquals(element.Style, infoStyle))
                        element.Tag = text;
                    element.ToolTip = CreateWrappedToolTip(text);
                }
                WrapToolTips(child);
            }
        }

        private static ToolTip CreateWrappedToolTip(string text) => new ToolTip
        {
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = ToolTipWidth }
        };

        private void InfoButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not string text) return;

            // A second click on the same button closes it (a click elsewhere already does, via StaysOpen).
            var wasThisButton = _openInfo != null && _openInfo.PlacementTarget == button && _openInfo.IsOpen;
            if (_openInfo != null) _openInfo.IsOpen = false;
            _openInfo = null;
            if (wasThisButton) return;

            // The pointer is on the button when it's clicked, so its hover tooltip is (or is about to be)
            // showing the same text right where this one appears -- close it and keep it from reopening
            // until the pinned one is dismissed.
            if (button.ToolTip is ToolTip hover) hover.IsOpen = false;
            ToolTipService.SetIsEnabled(button, false);

            var pinned = new ToolTip
            {
                Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = ToolTipWidth },
                PlacementTarget = button,
                Placement = PlacementMode.Bottom,
                StaysOpen = false
            };
            pinned.Closed += (s, args) =>
            {
                ToolTipService.SetIsEnabled(button, true);
                if (ReferenceEquals(_openInfo, pinned)) _openInfo = null;
            };
            _openInfo = pinned;
            pinned.IsOpen = true;
        }
    }
}
