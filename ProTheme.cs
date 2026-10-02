/*
 * Reads ArcGIS Pro's current application theme. Pro's theme resources aren't available to this
 * add-in's XAML at runtime, and switching themes needs a Pro restart, so callers can check this
 * once when a view is created.
 *
 * Ported from kyfromabove-ext's ProTheme.cs (same name, same logic) -- used by AddApiSourceDialog
 * to retemplate its catalog-picker ComboBox, since Pro's implicit ComboBox popup chrome ignores
 * locally-set Background/Foreground (confirmed there; see that dialog's own header comment).
 */
using System;
using System.Runtime.CompilerServices;
using ArcGIS.Desktop.Framework;

namespace KylidarAddin
{
    internal static class ProTheme
    {
        /// <summary>True when Pro is in its Dark theme. False for every other theme, or when Pro's framework isn't available.</summary>
        public static bool IsDark
        {
            get
            {
                try { return QueryDark(); }
                catch (Exception) { return false; }
            }
        }

        // Separate, non-inlined method so a missing Pro assembly surfaces inside IsDark's try block.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool QueryDark() => FrameworkApplication.ApplicationTheme == ApplicationTheme.Dark;
    }
}
