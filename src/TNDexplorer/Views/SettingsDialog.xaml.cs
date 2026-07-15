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
            SourceInitialized += (s, e) => Services.ThemeManager.ApplyToWindow(this);
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

        private void OnToggleAdvancedSettings(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as SettingsViewModel;
            if (vm == null) return;

            if (!vm.IsAdvancedEnabled)
            {
                // Enabling advanced settings: show warning dialog
                var result = MessageBox.Show(
                    "高度な設定を変更すると、ネットワークおよびシステム全体に\r\n" +
                    "重大な影響を与える可能性があります。\r\n\r\n" +
                    "・短い間隔でのリスキャン：\r\n" +
                    "  ネットワーク帯域を継続的に圧迫し、他のアプリケーションの\r\n" +
                    "  通信速度低下や、接続先サーバーへの過度な負荷（アクセス制限等）\r\n" +
                    "  を招く恐れがあります。\r\n\r\n" +
                    "・システムファイル等のスキャン：\r\n" +
                    "  スキャン処理が著しく長期化するほか、意図しないファイルロック\r\n" +
                    "  などによりPCの動作が不安定になる可能性があります。\r\n\r\n" +
                    "システムへの影響を完全に理解している場合のみ、有効化してください。",
                    "高度な設定の有効化に関する警告",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No); // Default to "No" (cancel)

                if (result == MessageBoxResult.Yes)
                {
                    vm.IsAdvancedEnabled = true;
                }
            }
            else
            {
                // Disabling advanced settings: no confirmation needed, auto-fallback in VM
                vm.IsAdvancedEnabled = false;
            }
        }

        private void OnRescanSelectedClick(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as SettingsViewModel;
            if (vm == null) return;

            var targets = vm.CustomFolders.Where(f => f.IsSelected).ToList();
            if (targets.Count == 0)
            {
                MessageBox.Show("更新するフォルダがありません。走査対象のフォルダにチェックを入れてください。",
                    "リスキャン", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (vm.RescanCustomFolderAction != null)
            {
                foreach (var target in targets)
                {
                    vm.RescanCustomFolderAction(target.FolderPath);
                }
                MessageBox.Show($"{targets.Count} 件のフォルダの更新を開始しました。バックグラウンドで実行されます。",
                    "リスキャン", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show("リスキャンを実行するにはアプリケーションの再起動が必要です。",
                    "リスキャン", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OnManualRescanClick(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as SettingsViewModel;
            if (vm?.ManualRescanAction != null)
            {
                vm.ManualRescanAction();
                MessageBox.Show("リスキャンを開始しました。バックグラウンドで実行されます。",
                    "リスキャン", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show("リスキャンを実行するにはアプリケーションの再起動が必要です。",
                    "リスキャン", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OnRemoveFolderRow(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as SettingsViewModel;
            var item = (sender as FrameworkElement)?.DataContext as CustomFolderItem;
            if (vm == null || item == null) return;

            var result = MessageBox.Show(
                $"「{item.FolderPath}」を削除しますか？",
                "フォルダの削除",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                vm.RemoveCustomFolders(new List<CustomFolderItem> { item });
            }
        }
    }
}
