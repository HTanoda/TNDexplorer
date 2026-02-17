using System.Windows;
using TNDexplorer.ViewModels;

namespace TNDexplorer.Views
{
    public partial class SetupWizardDialog : Window
    {
        public SetupWizardDialog()
        {
            InitializeComponent();
        }

        private void OnStartClick(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as SetupWizardViewModel;
            vm?.SaveCommand.Execute(null);
            DialogResult = true;
            Close();
        }
    }
}
