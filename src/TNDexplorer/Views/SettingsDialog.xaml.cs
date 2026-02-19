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

            var dialog = new FolderBrowseDialog
            {
                Owner = this,
                Description = "フォルダを選択",
                SelectedPath = vm.NewSettingsFavFolder ?? ""
            };
            if (dialog.ShowDialog() == true)
            {
                vm.NewSettingsFavFolder = dialog.SelectedPath;
            }
        }

        private void OnAddCustomFolder(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as SettingsViewModel;
            if (vm == null) return;

            var dialog = new FolderBrowseDialog
            {
                Owner = this,
                Description = "走査対象フォルダを選択"
            };
            if (dialog.ShowDialog() == true)
            {
                vm.AddCustomFolder(dialog.SelectedPath);
            }
        }

        private void OnRemoveCustomFolder(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as SettingsViewModel;
            if (vm == null) return;

            var selectedItems = vm.CustomFolders.Where(f => f.IsSelected).ToList();
            if (selectedItems.Count == 0)
            {
                // If none checked, remove all unchecked (interpretation: remove selected via checkbox)
                // Better: show message
                MessageBox.Show("削除するフォルダのチェックを外して選択してください。", "フォルダ",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var result = MessageBox.Show(
                $"選択した {selectedItems.Count} 件のフォルダを削除しますか？",
                "フォルダの削除",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                vm.RemoveCustomFolders(selectedItems);
            }
        }
    }
}
