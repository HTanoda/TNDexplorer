using System.Windows;

namespace TNDexplorer.Views
{
    public partial class FolderBrowseDialog : Window
    {
        public string SelectedPath
        {
            get => PathTextBox.Text?.Trim();
            set => PathTextBox.Text = value;
        }

        public string Description { get; set; }

        public FolderBrowseDialog()
        {
            InitializeComponent();
        }

        private void OnBrowseClick(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = Description ?? "フォルダを選択";
                dialog.ShowNewFolderButton = false;
                if (!string.IsNullOrWhiteSpace(PathTextBox.Text)
                    && System.IO.Directory.Exists(PathTextBox.Text.Trim()))
                {
                    dialog.SelectedPath = PathTextBox.Text.Trim();
                }
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    PathTextBox.Text = dialog.SelectedPath;
                }
            }
        }

        private void OnOkClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(PathTextBox.Text))
            {
                MessageBox.Show("フォルダパスを入力してください。", "フォルダの選択",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            DialogResult = true;
            Close();
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
