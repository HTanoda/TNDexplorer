using System.Windows;

namespace TNDexplorer.Views
{
    public partial class AboutDialog : Window
    {
        public AboutDialog()
        {
            InitializeComponent();
            SourceInitialized += (s, e) => Services.ThemeManager.ApplyToWindow(this);
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
