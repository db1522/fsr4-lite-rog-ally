using System;
using System.IO;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace RalFsr.Widget
{
    /// <summary>
    /// Catches widget errors so they show inside the widget, with details, instead of Game Bar's generic
    /// "something isn't playing nice". Everything is also appended to widget.log in the app's LocalState folder.
    /// </summary>
    internal static class CrashLog
    {
        private static readonly object Gate = new object();

        public static string LogPath
        {
            get { return Path.Combine(ApplicationData.Current.LocalFolder.Path, "widget.log"); }
        }

        public static void Write(string where, Exception ex)
        {
            try
            {
                lock (Gate)
                {
                    File.AppendAllText(LogPath,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + where + "] " + ex + Environment.NewLine);
                }
            }
            catch (Exception)
            {
                // Logging must never cause a second crash.
            }
        }

        /// <summary>Replaces the widget's content with the error and where to find the log.</summary>
        public static void Show(string where, Exception ex)
        {
            try
            {
                var text = new TextBlock
                {
                    Text = "FSR 4 Lite hit an error (" + where + ").\n\n" +
                           ex.GetType().Name + ": " + ex.Message + "\n\n" +
                           "Details were saved to:\n" + LogPath,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                    Margin = new Thickness(20),
                    FontSize = 14,
                };
                Window.Current.Content = new ScrollViewer
                {
                    Content = text,
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xF2, 0x1B, 0x1D, 0x22)),
                };
                Window.Current.Activate();
            }
            catch (Exception)
            {
                // Nothing more can be done if the window itself is unusable.
            }
        }
    }
}
