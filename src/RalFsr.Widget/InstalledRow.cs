using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;

namespace RalFsr.Widget
{
    public sealed class InstalledRow
    {
        public string Name { get; set; }
        public string ExeDir { get; set; }
        public string Note { get; set; }

        /// <summary>A short tag on the right of the row ("Running", "Update"), or hidden when empty.</summary>
        public string Status { get; set; } = "";
        public Visibility StatusVisibility => string.IsNullOrEmpty(Status) ? Visibility.Collapsed : Visibility.Visible;
        public Brush StatusForeground { get; set; }
        public Brush StatusBackground { get; set; }
    }
}
