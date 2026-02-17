using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TNDexplorer.Core;
using TNDexplorer.ViewModels;

namespace TNDexplorer.Views
{
    public partial class MainWindow : Window
    {
        private GridViewColumnHeader _lastHeaderClicked;
        private ListSortDirection _lastDirection = ListSortDirection.Ascending;
        private PreviewHandlerHost _previewHost;
        private string _currentPreviewPath;
        private bool _isPdfPreview;
        private CancellationTokenSource _pdfRenderCts;

        // PDF virtualization state
        private PdfDocumentHandle _pdfDocHandle;
        private readonly Dictionary<int, BitmapSource> _pdfPageCache = new Dictionary<int, BitmapSource>();
        private readonly LinkedList<int> _pdfCacheLru = new LinkedList<int>();
        private const int PdfCacheMaxPages = 12;
        private const int PdfPrefetchAhead = 2; // pages to prefetch ahead/behind visible area

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
            SizeChanged += MainWindow_SizeChanged;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);

            // Initially hide preview column
            PreviewColumn.Width = new GridLength(0);
            PreviewColumn.MinWidth = 0;

            var vm = DataContext as MainViewModel;
            if (vm != null)
            {
                _previewHost = new PreviewHandlerHost();
                vm.PreviewFileChanged += OnPreviewFileChanged;
                vm.PreviewVisibilityChanged += OnPreviewVisibilityChanged;
            }
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);

            if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                var vm = DataContext as MainViewModel;
                if (vm != null) vm.SearchText = "";
                SearchBox.Focus();
                Keyboard.Focus(SearchBox);
                SearchBox.SelectAll();
                e.Handled = true;
            }
        }

        private void OnPreviewVisibilityChanged()
        {
            var vm = DataContext as MainViewModel;
            if (vm != null && !vm.IsPreviewVisible)
            {
                CancelPdfRendering();
                _previewHost?.UnloadPreview();
                _currentPreviewPath = null;
                _isPdfPreview = false;
                PdfScrollViewer.Visibility = Visibility.Collapsed;
                PdfPagesPanel.Children.Clear();
                PreviewPlaceholder.Visibility = Visibility.Visible;
                // Collapse the preview column
                PreviewColumn.Width = new GridLength(0);
                PreviewColumn.MinWidth = 0;
            }
            else if (vm != null && vm.IsPreviewVisible)
            {
                // Expand the preview column
                PreviewColumn.Width = new GridLength(300);
                PreviewColumn.MinWidth = 150;

                if (vm.SelectedResult != null && !vm.SelectedResult.IsDirectory)
                {
                    // Delay to allow layout to complete before showing preview
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        OnPreviewFileChanged(vm.SelectedResult.FullPath);
                    }), System.Windows.Threading.DispatcherPriority.Loaded);
                }
            }
        }

        private void OnPreviewFileChanged(string filePath)
        {
            if (_previewHost == null) return;

            // Reset all preview state
            CancelPdfRendering();
            _previewHost.UnloadPreview();
            _currentPreviewPath = null;
            _isPdfPreview = false;
            PdfScrollViewer.Visibility = Visibility.Collapsed;
            PdfPagesPanel.Children.Clear();
            PreviewPlaceholder.Text = "ファイルを選択するとプレビューが表示されます";
            PreviewPlaceholder.Visibility = Visibility.Visible;

            if (string.IsNullOrEmpty(filePath))
            {
                return;
            }

            // PDF files: use WinRT-based PdfRenderer (works in admin processes)
            if (PreviewHandlerHost.IsPdfFile(filePath))
            {
                ShowPdfPreview(filePath);
                return;
            }

            // Image files: display directly with WPF Image control
            if (IsImageFile(filePath))
            {
                ShowImagePreview(filePath);
                return;
            }

            // Non-PDF/image files: use IPreviewHandler COM
            // Delay to ensure layout is complete before creating child HWND
            string pathCopy = filePath;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ShowComPreview(pathCopy);
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>
        /// Shows a file preview using the IPreviewHandler COM interface.
        /// Called via Dispatcher.BeginInvoke to ensure layout is complete.
        /// </summary>
        private void ShowComPreview(string filePath)
        {
            if (_previewHost == null) return;

            var hwndSource = PresentationSource.FromVisual(this) as HwndSource;
            if (hwndSource == null) return;
            IntPtr parentHwnd = hwndSource.Handle;

            var borderRect = GetElementRectInWindow(PreviewHostBorder);
            if (borderRect.Width <= 0 || borderRect.Height <= 0) return;

            var dpi = VisualTreeHelper.GetDpi(this);
            int left = (int)(borderRect.X * dpi.DpiScaleX);
            int top = (int)(borderRect.Y * dpi.DpiScaleY);
            int width = (int)(borderRect.Width * dpi.DpiScaleX);
            int height = (int)(borderRect.Height * dpi.DpiScaleY);

            string previewError = null;
            bool success = _previewHost.ShowPreview(filePath, parentHwnd, left, top, width, height, out previewError);
            if (success)
            {
                _currentPreviewPath = filePath;
                PreviewPlaceholder.Visibility = Visibility.Collapsed;
            }
            else
            {
                PreviewPlaceholder.Text = string.IsNullOrEmpty(previewError)
                    ? "このファイル形式はプレビューできません"
                    : $"プレビューに失敗しました\n{previewError}";
                PreviewPlaceholder.Visibility = Visibility.Visible;
            }
        }

        private static readonly string[] ImageExtensions = {
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tiff", ".tif",
            ".ico", ".webp", ".wdp", ".hdp", ".jfif"
        };

        private static bool IsImageFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return false;
            string ext = System.IO.Path.GetExtension(filePath);
            if (string.IsNullOrEmpty(ext)) return false;
            ext = ext.ToLowerInvariant();
            foreach (var ie in ImageExtensions)
            {
                if (ext == ie) return true;
            }
            return false;
        }

        /// <summary>
        /// Displays an image file directly using WPF's built-in BitmapImage.
        /// </summary>
        private void ShowImagePreview(string filePath)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(filePath);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();

                var image = new Image
                {
                    Source = bitmap,
                    Stretch = Stretch.Uniform,
                    StretchDirection = StretchDirection.DownOnly,
                };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

                PdfPagesPanel.Children.Clear();
                PdfPagesPanel.Children.Add(image);
                PdfScrollViewer.Visibility = Visibility.Visible;
                PreviewPlaceholder.Visibility = Visibility.Collapsed;
                _currentPreviewPath = filePath;
                _isPdfPreview = true; // reuse flag to skip child HWND repositioning
            }
            catch
            {
                PreviewPlaceholder.Text = "この画像はプレビューできません";
                PreviewPlaceholder.Visibility = Visibility.Visible;
            }
        }

        // Track last rendered width so we know when to re-render on resize
        private double _pdfLastPanelWidth;
        private System.Windows.Threading.DispatcherTimer _pdfResizeTimer;

        /// <summary>
        /// Shows a virtualized PDF preview. Opens the PDF document, creates placeholder
        /// elements for each page, and renders only visible pages on demand.
        /// Uses an LRU cache to limit memory usage.
        /// </summary>
        private void ShowPdfPreview(string filePath)
        {
            PreviewPlaceholder.Text = "PDFを読み込み中...";
            PreviewPlaceholder.Visibility = Visibility.Visible;

            _pdfRenderCts = new CancellationTokenSource();
            var ct = _pdfRenderCts.Token;

            Task.Run(() =>
            {
                if (ct.IsCancellationRequested) return;
                PdfDocumentHandle docHandle = null;
                try
                {
                    docHandle = PdfRenderer.OpenDocument(filePath);
                }
                catch { }

                if (docHandle == null || ct.IsCancellationRequested)
                {
                    docHandle?.Dispose();
                    if (!ct.IsCancellationRequested)
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            PreviewPlaceholder.Text = "PDFのプレビューに失敗しました";
                            PreviewPlaceholder.Visibility = Visibility.Visible;
                        }));
                    }
                    return;
                }

                var localHandle = docHandle;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (ct.IsCancellationRequested)
                    {
                        localHandle.Dispose();
                        return;
                    }

                    // Store the handle for on-demand rendering
                    _pdfDocHandle = localHandle;
                    _pdfPageCache.Clear();
                    _pdfCacheLru.Clear();
                    _currentPreviewPath = filePath;
                    _isPdfPreview = true;

                    // Build placeholders and render visible pages
                    RebuildPdfLayout();

                    PdfScrollViewer.Visibility = Visibility.Visible;
                    PreviewPlaceholder.Visibility = Visibility.Collapsed;

                    // Attach scroll event for virtualization
                    PdfScrollViewer.ScrollChanged -= PdfScrollViewer_ScrollChanged;
                    PdfScrollViewer.ScrollChanged += PdfScrollViewer_ScrollChanged;

                    // Render initially visible pages
                    RenderVisiblePdfPages();
                }));
            }, ct);
        }

        /// <summary>
        /// Calculates the available panel width for PDF pages.
        /// </summary>
        private double GetPdfPanelWidth()
        {
            double w = PdfScrollViewer.ViewportWidth;
            if (w <= 0) w = PdfScrollViewer.ActualWidth;
            if (w <= 0) w = PreviewHostBorder.ActualWidth;
            if (w <= 0) w = PreviewColumn.ActualWidth;
            if (w <= 0) w = 300;
            double panelWidth = w - 18; // scrollbar
            return panelWidth < 80 ? 80 : panelWidth;
        }

        /// <summary>
        /// Rebuilds PDF page placeholders to fit the current panel width.
        /// Clears the render cache so pages will be re-rendered at the new size.
        /// </summary>
        private void RebuildPdfLayout()
        {
            if (_pdfDocHandle == null) return;

            double panelWidth = GetPdfPanelWidth();
            _pdfLastPanelWidth = panelWidth;

            // Update render width for crisp output (2x for HiDPI)
            var dpi = VisualTreeHelper.GetDpi(this);
            _pdfDocHandle.RenderWidth = (uint)(panelWidth * dpi.DpiScaleX * 2);

            // Clear cache — bitmaps are rendered at the old width
            _pdfPageCache.Clear();
            _pdfCacheLru.Clear();

            PdfPagesPanel.Children.Clear();
            for (int i = 0; i < _pdfDocHandle.PageCount; i++)
            {
                var pageSize = _pdfDocHandle.PageSizes[i];
                double aspectRatio = pageSize.Height / pageSize.Width;
                double pageHeight = panelWidth * aspectRatio;

                var placeholder = new Border
                {
                    Width = panelWidth,
                    Height = pageHeight,
                    Background = System.Windows.Media.Brushes.White,
                    BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(200, 200, 200)),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Margin = new Thickness(0, 0, 0, 2),
                    Tag = i,
                };

                placeholder.Child = new TextBlock
                {
                    Text = $"ページ {i + 1}",
                    Foreground = System.Windows.Media.Brushes.LightGray,
                    FontSize = 14,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                PdfPagesPanel.Children.Add(placeholder);
            }
        }

        /// <summary>
        /// Handles scroll events on the PDF preview ScrollViewer.
        /// Also detects width changes (from splitter drag) and triggers re-layout.
        /// </summary>
        private void PdfScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_pdfDocHandle == null || !_isPdfPreview) return;

            // Check if width changed (splitter dragged)
            if (e.ViewportWidthChange != 0)
            {
                SchedulePdfResize();
                return;
            }

            RenderVisiblePdfPages();
        }

        /// <summary>
        /// Schedules a debounced PDF re-layout after the panel is resized.
        /// </summary>
        private void SchedulePdfResize()
        {
            if (_pdfResizeTimer == null)
            {
                _pdfResizeTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(200),
                };
                _pdfResizeTimer.Tick += (s, e2) =>
                {
                    _pdfResizeTimer.Stop();
                    if (_pdfDocHandle == null || !_isPdfPreview) return;

                    double newWidth = GetPdfPanelWidth();
                    if (Math.Abs(newWidth - _pdfLastPanelWidth) < 5) return; // ignore tiny changes

                    RebuildPdfLayout();
                    RenderVisiblePdfPages();
                };
            }
            _pdfResizeTimer.Stop();
            _pdfResizeTimer.Start();
        }

        /// <summary>
        /// Determines which PDF pages are visible in the ScrollViewer's viewport,
        /// and renders them (plus a prefetch buffer) on a background thread.
        /// </summary>
        private void RenderVisiblePdfPages()
        {
            if (_pdfDocHandle == null) return;

            double viewportTop = PdfScrollViewer.VerticalOffset;
            double viewportBottom = viewportTop + PdfScrollViewer.ViewportHeight;
            if (viewportBottom <= 0) return;

            // Find visible page indices
            int firstVisible = -1;
            int lastVisible = -1;
            double cumulativeTop = 0;

            for (int i = 0; i < PdfPagesPanel.Children.Count; i++)
            {
                var child = PdfPagesPanel.Children[i] as FrameworkElement;
                if (child == null) continue;

                double childHeight = child.ActualHeight > 0 ? child.ActualHeight : ((Border)child).Height;
                double childBottom = cumulativeTop + childHeight + 2; // +2 for margin

                if (childBottom > viewportTop && cumulativeTop < viewportBottom)
                {
                    if (firstVisible < 0) firstVisible = i;
                    lastVisible = i;
                }

                cumulativeTop = childBottom;
                if (cumulativeTop > viewportBottom && lastVisible >= 0)
                    break;
            }

            if (firstVisible < 0) return;

            // Expand range with prefetch buffer
            int renderFrom = Math.Max(0, firstVisible - PdfPrefetchAhead);
            int renderTo = Math.Min(PdfPagesPanel.Children.Count - 1, lastVisible + PdfPrefetchAhead);

            var cts = _pdfRenderCts;
            if (cts == null) return;
            var ct = cts.Token;

            // Render pages that are not yet cached
            for (int i = renderFrom; i <= renderTo; i++)
            {
                if (_pdfPageCache.ContainsKey(i)) continue;
                if (ct.IsCancellationRequested) return;

                int pageIndex = i;

                // Mark as "rendering" to prevent duplicate requests
                _pdfPageCache[pageIndex] = null;

                // Capture handle reference for the background task
                var docHandle = _pdfDocHandle;
                if (docHandle == null) return;

                Task.Run(() =>
                {
                    if (ct.IsCancellationRequested) return;
                    System.Drawing.Bitmap bitmap = null;
                    try
                    {
                        bitmap = docHandle.RenderPage(pageIndex);
                    }
                    catch { }

                    if (bitmap == null || ct.IsCancellationRequested)
                    {
                        bitmap?.Dispose();
                        return;
                    }

                    IntPtr hBitmap = IntPtr.Zero;
                    try
                    {
                        hBitmap = bitmap.GetHbitmap();
                        var bitmapSource = Imaging.CreateBitmapSourceFromHBitmap(
                            hBitmap, IntPtr.Zero, Int32Rect.Empty,
                            BitmapSizeOptions.FromEmptyOptions());
                        bitmapSource.Freeze();

                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (ct.IsCancellationRequested) return;
                            SetPdfPageImage(pageIndex, bitmapSource);
                        }));
                    }
                    catch { }
                    finally
                    {
                        if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
                        bitmap.Dispose();
                    }
                });
            }
        }

        /// <summary>
        /// Sets the rendered bitmap for a PDF page, replacing the placeholder.
        /// Manages LRU cache eviction.
        /// </summary>
        private void SetPdfPageImage(int pageIndex, BitmapSource bitmapSource)
        {
            if (pageIndex < 0 || pageIndex >= PdfPagesPanel.Children.Count) return;
            if (_pdfDocHandle == null) return;

            // Update LRU cache
            _pdfPageCache[pageIndex] = bitmapSource;
            _pdfCacheLru.Remove(pageIndex);
            _pdfCacheLru.AddLast(pageIndex);

            // Evict oldest pages if cache is full
            while (_pdfCacheLru.Count > PdfCacheMaxPages)
            {
                int evictIdx = _pdfCacheLru.First.Value;
                _pdfCacheLru.RemoveFirst();
                _pdfPageCache.Remove(evictIdx);

                if (evictIdx >= 0 && evictIdx < PdfPagesPanel.Children.Count)
                {
                    var evictBorder = PdfPagesPanel.Children[evictIdx] as Border;
                    if (evictBorder != null)
                    {
                        evictBorder.Child = new TextBlock
                        {
                            Text = $"ページ {evictIdx + 1}",
                            Foreground = System.Windows.Media.Brushes.LightGray,
                            FontSize = 14,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                        };
                    }
                }
            }

            // Replace placeholder with the rendered image
            var border = PdfPagesPanel.Children[pageIndex] as Border;
            if (border != null)
            {
                var image = new System.Windows.Controls.Image
                {
                    Source = bitmapSource,
                    Stretch = Stretch.Uniform,
                    StretchDirection = StretchDirection.DownOnly,
                };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                border.Child = image;
            }
        }

        private void CancelPdfRendering()
        {
            PdfScrollViewer.ScrollChanged -= PdfScrollViewer_ScrollChanged;

            if (_pdfResizeTimer != null)
            {
                _pdfResizeTimer.Stop();
            }

            if (_pdfRenderCts != null)
            {
                try { _pdfRenderCts.Cancel(); } catch { }
                _pdfRenderCts = null;
            }

            if (_pdfDocHandle != null)
            {
                _pdfDocHandle.Dispose();
                _pdfDocHandle = null;
            }

            _pdfPageCache.Clear();
            _pdfCacheLru.Clear();
        }

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool DeleteObject(IntPtr hObject);

        /// <summary>
        /// Gets the bounding rectangle of a UI element relative to the window's client area.
        /// </summary>
        private Rect GetElementRectInWindow(FrameworkElement element)
        {
            try
            {
                var transform = element.TransformToAncestor(this);
                var topLeft = transform.Transform(new Point(0, 0));
                return new Rect(topLeft.X, topLeft.Y, element.ActualWidth, element.ActualHeight);
            }
            catch
            {
                return Rect.Empty;
            }
        }

        private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdatePreviewPosition();
        }

        /// <summary>
        /// Called when the GridSplitter is dragged or window resized,
        /// to update the preview child HWND position or re-layout PDF pages.
        /// </summary>
        private void PreviewHostBorder_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_isPdfPreview && _pdfDocHandle != null)
            {
                SchedulePdfResize();
            }
            else
            {
                UpdatePreviewPosition();
            }
        }

        private void UpdatePreviewPosition()
        {
            if (_previewHost == null || _currentPreviewPath == null) return;

            // PDF preview uses WPF Image control which auto-resizes, no child HWND to move
            if (_isPdfPreview) return;

            var hwndSource = PresentationSource.FromVisual(this) as HwndSource;
            if (hwndSource == null) return;

            var borderRect = GetElementRectInWindow(PreviewHostBorder);
            if (borderRect.Width <= 0 || borderRect.Height <= 0) return;

            var dpi = VisualTreeHelper.GetDpi(this);
            int left = (int)(borderRect.X * dpi.DpiScaleX);
            int top = (int)(borderRect.Y * dpi.DpiScaleY);
            int width = (int)(borderRect.Width * dpi.DpiScaleX);
            int height = (int)(borderRect.Height * dpi.DpiScaleY);

            _previewHost.Reposition(left, top, width, height);
        }

        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            CancelPdfRendering();
            _previewHost?.Dispose();

            var vm = DataContext as MainViewModel;
            vm?.Dispose();

            Application.Current.Shutdown();
            Environment.Exit(0);
        }

        private void ResultsListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var vm = DataContext as MainViewModel;
            vm?.OnItemDoubleClick();
        }

        private void GridViewColumnHeader_Click(object sender, RoutedEventArgs e)
        {
            var headerClicked = e.OriginalSource as GridViewColumnHeader;
            if (headerClicked == null || headerClicked.Role == GridViewColumnHeaderRole.Padding)
                return;

            ListSortDirection direction;
            if (headerClicked != _lastHeaderClicked)
            {
                direction = ListSortDirection.Ascending;
            }
            else
            {
                direction = _lastDirection == ListSortDirection.Ascending
                    ? ListSortDirection.Descending
                    : ListSortDirection.Ascending;
            }

            var binding = headerClicked.Column.DisplayMemberBinding as Binding;
            string sortBy = binding?.Path?.Path;
            if (string.IsNullOrEmpty(sortBy)) return;

            var dataView = CollectionViewSource.GetDefaultView(ResultsListView.ItemsSource);
            if (dataView == null) return;

            dataView.SortDescriptions.Clear();
            dataView.SortDescriptions.Add(new SortDescription(sortBy, direction));
            dataView.Refresh();

            if (_lastHeaderClicked != null)
                _lastHeaderClicked.Column.HeaderStringFormat = null;

            headerClicked.Column.HeaderStringFormat = direction == ListSortDirection.Ascending
                ? "{0} ▲" : "{0} ▼";

            _lastHeaderClicked = headerClicked;
            _lastDirection = direction;
        }
    }
}
