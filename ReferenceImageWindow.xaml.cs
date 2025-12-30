using System.Windows;
using NVSPlotter.Services;

namespace NVSPlotter
{
    public partial class ReferenceImageWindow : Window
    {
        public ReferenceImageWindow()
        {
            InitializeComponent();
            ApplyTheme(ThemeManager.Instance.IsDarkMode);
            ThemeManager.Instance.ThemeChanged += (_, isDark) =>
            {
                Dispatcher.Invoke(() => ApplyTheme(isDark));
            };
        }

        public void SetContent(UIElement content)
        {
            HostContent.Content = content;
        }

        private void ApplyTheme(bool isDark)
        {
            // Replace dynamic resources with the appropriate resource dictionary entries
            var key = isDark ? "DarkReferenceResources" : "LightReferenceResources";
            if (TryFindResource(key) is ResourceDictionary rd)
            {
                // Transfer brushes into dynamic resources
                foreach (var kv in rd.Keys)
                {
                    var obj = rd[kv];
                    Resources[kv] = obj;
                }
            }
        }
    }
}
