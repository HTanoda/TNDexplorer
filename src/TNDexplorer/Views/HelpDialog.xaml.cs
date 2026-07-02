using System.Windows;

namespace TNDexplorer.Views
{
    public partial class HelpDialog : Window
    {
        public HelpDialog()
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
