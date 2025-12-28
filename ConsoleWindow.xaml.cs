using System;
using System.ComponentModel;
using System.Windows;

namespace NVSPlotter
{
    public partial class ConsoleWindow : Window
    {
        public ConsoleWindow()
        {
            InitializeComponent();
        }

        public void AppendLog(string message)
        {
            if (ConsoleBox == null) return;

            var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            ConsoleBox.AppendText(line + Environment.NewLine);
            
            if (AutoScrollCheck?.IsChecked == true)
            {
                ConsoleBox.ScrollToEnd();
            }
        }

        private void ClearBtn_Click(object sender, RoutedEventArgs e)
        {
            ConsoleBox?.Clear();
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            // Hide instead of close so we can show it again
            e.Cancel = true;
            Hide();
        }

        public void ForceClose()
        {
            // Allow actual closing when main window closes
            Closing -= Window_Closing;
            Close();
        }
    }
}
