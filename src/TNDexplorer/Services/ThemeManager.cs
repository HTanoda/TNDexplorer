using System;
using System.Windows;

namespace TNDexplorer.Services
{
    /// <summary>
    /// アプリのテーマ（Light / Dark）を切り替える。
    /// Application.Current.Resources.MergedDictionaries 内のテーマ辞書
    /// (Themes/Light.xaml または Themes/Dark.xaml) を差し替える。
    /// 冪等: 既に対象テーマが適用済みなら何もしない。
    /// </summary>
    public static class ThemeManager
    {
        private static readonly Uri LightUri =
            new Uri("pack://application:,,,/Themes/Light.xaml", UriKind.Absolute);
        private static readonly Uri DarkUri =
            new Uri("pack://application:,,,/Themes/Dark.xaml", UriKind.Absolute);

        public const string Light = "Light";
        public const string Dark = "Dark";

        public static string Normalize(string theme)
        {
            return string.Equals(theme, Dark, StringComparison.OrdinalIgnoreCase) ? Dark : Light;
        }

        public static void Apply(string theme)
        {
            var app = Application.Current;
            if (app == null) return;

            theme = Normalize(theme);
            Uri targetUri = theme == Dark ? DarkUri : LightUri;

            var dictionaries = app.Resources.MergedDictionaries;

            // 既存のテーマ辞書 (Light.xaml / Dark.xaml) を探す
            ResourceDictionary existing = null;
            foreach (var d in dictionaries)
            {
                if (IsThemeDictionary(d))
                {
                    existing = d;
                    break;
                }
            }

            // 既に対象テーマなら何もしない（冪等）
            if (existing != null && existing.Source != null &&
                existing.Source.Equals(targetUri))
            {
                return;
            }

            var newTheme = new ResourceDictionary { Source = targetUri };

            if (existing != null)
            {
                int index = dictionaries.IndexOf(existing);
                dictionaries[index] = newTheme;
            }
            else
            {
                dictionaries.Insert(0, newTheme);
            }
        }

        private static bool IsThemeDictionary(ResourceDictionary d)
        {
            if (d?.Source == null) return false;
            string s = d.Source.OriginalString;
            if (string.IsNullOrEmpty(s)) return false;
            return s.EndsWith("Themes/Light.xaml", StringComparison.OrdinalIgnoreCase)
                || s.EndsWith("Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase);
        }
    }
}
