using System.Configuration;
using System.Data;
using System.Windows;
using NVSPlotter.Services;

// Avoid ambiguity with System.Windows.Forms types
using Application = System.Windows.Application;

namespace NVSPlotter
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            
            // Initialize theme manager and apply theme based on settings/system preference
            ThemeManager.Instance.Initialize();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // Save theme settings and cleanup
            ThemeManager.Instance.SaveSettings();
            ThemeManager.Instance.Cleanup();
            
            base.OnExit(e);
        }
    }

}
