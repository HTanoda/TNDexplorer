using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using TNDexplorer.ViewModels;

namespace TNDexplorer.Views
{
    public partial class SettingsDialog : Window
    {
        public SettingsDialog()
        {
            InitializeComponent();
        }

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as SettingsViewModel;
            vm?.SaveCommand.Execute(null);
            DialogResult = true;
            Close();
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void OnFavoritesSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Enable Edit button only when exactly one item is selected
            EditFavoriteButton.IsEnabled = FavoritesListView.SelectedItems.Count == 1;
        }

        private void OnEditSelectedFavorite(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as SettingsViewModel;
            if (vm == null) return;

            if (FavoritesListView.SelectedItems.Count != 1) return;

            var selected = FavoritesListView.SelectedItem as SettingsFavoriteItem;
            if (selected == null) return;

            vm.BeginEditFavorite(selected);
        }

        private void OnCancelEditFavorite(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as SettingsViewModel;
            vm?.CancelEditFavorite();
        }

        private void OnDeleteSelectedFavorites(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as SettingsViewModel;
            if (vm == null) return;

            var selected = FavoritesListView.SelectedItems
                .Cast<SettingsFavoriteItem>()
                .ToList();

            if (selected.Count == 0)
            {
                MessageBox.Show("削除するお気に入りを選択してください。", "お気に入り",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            foreach (var item in selected)
            {
                vm.SettingsFavorites.Remove(item);
            }
        }

        private void OnAddSettingsFavorite(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as SettingsViewModel;
            vm?.AddOrUpdateSettingsFavorite();
        }

        private void OnBrowseNewFavFolder(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as SettingsViewModel;
            if (vm == null) return;

            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "フォルダを選択";
                dialog.ShowNewFolderButton = false;
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    vm.NewSettingsFavFolder = dialog.SelectedPath;
                }
            }
        }
    }
}
