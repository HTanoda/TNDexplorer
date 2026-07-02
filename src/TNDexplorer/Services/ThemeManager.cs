using System;
using System.Windows;
using System.Windows.Interop;
using TNDexplorer.Native;

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

            // タイトルバーの暗色/明色を全ウィンドウへ即時反映
            _isDark = theme == Dark;
            foreach (Window w in app.Windows)
            {
                ApplyToWindow(w);
            }
        }

        private static bool _isDark;

        /// <summary>
        /// ウィンドウのタイトルバーを現在のテーマに合わせて暗色/明色化する (DWM)。
        /// ハンドル未生成時は SourceInitialized で自動的に再適用する。
        /// </summary>
        public static void ApplyToWindow(Window window)
        {
            if (window == null) return;

            var helper = new WindowInteropHelper(window);
            IntPtr hwnd = helper.Handle;

            if (hwnd == IntPtr.Zero)
            {
                // ハンドル未生成: 生成後に一度だけ適用
                EventHandler handler = null;
                handler = (s, e) =>
                {
                    window.SourceInitialized -= handler;
                    ApplyDwmDarkMode(new WindowInteropHelper(window).Handle);
                };
                window.SourceInitialized += handler;
                return;
            }

            ApplyDwmDarkMode(hwnd);
        }

        private static void ApplyDwmDarkMode(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;

            int useDark = _isDark ? 1 : 0;
            try
            {
                int hr = NativeMethods.DwmSetWindowAttribute(
                    hwnd,
                    NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE,
                    ref useDark,
                    sizeof(int));

                if (hr != 0)
                {
                    // 旧ビルド (Win10 1809〜1903) は属性 ID 19
                    NativeMethods.DwmSetWindowAttribute(
                        hwnd,
                        NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1,
                        ref useDark,
                        sizeof(int));
                }
            }
            catch
            {
                // DWM 未対応環境は無視
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
