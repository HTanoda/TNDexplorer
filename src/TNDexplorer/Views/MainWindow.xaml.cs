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
using System.Diagnostics;
using System.Linq;
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
        private readonly List<Task> _pdfRenderTasks = new List<Task>();

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
                vm.FileOpening += OnFileOpening;

                // Restore saved column order
                RestoreColumnOrder(vm);
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
            PdfScrollViewer.ScrollToVerticalOffset(0);
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

            // PowerPoint conflict: the preview handler's LocalServer32 is POWERPNT.EXE
            // itself. When PowerPoint is already running, COM connects to the running
            // instance and the activation/initialization hangs. Skip entirely.
            if (IsPowerPointFile(filePath) && IsPowerPointRunning())
            {
                PreviewPlaceholder.Text =
                    "PowerPointが起動中のため、このファイルの\n"
                    + "プレビューを表示できません。\n\n"
                    + "PowerPointを閉じてから再度お試しください。";
                PreviewPlaceholder.Visibility = Visibility.Visible;
                return;
            }

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
                // E_FAIL (0x80004005) and E_NOTIMPL (0x80004001) are typically caused by
                // Zone Identifier security block on downloaded files.
                if (!string.IsNullOrEmpty(previewError)
                    && (previewError.IndexOf("80004005", StringComparison.OrdinalIgnoreCase) >= 0
                        || previewError.IndexOf("80004001", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    PreviewPlaceholder.Text =
                        "このファイルはセキュリティブロックされている\n"
                        + "可能性があります。\n\n"
                        + "ファイルのプロパティ → 全般タブ →\n"
                        + "「許可する」にチェックを入れてから\n"
                        + "再度お試しください。";
                }
                // E_NOINTERFACE (0x80004002) occurs when COM handler is unavailable
                else if (!string.IsNullOrEmpty(previewError)
                    && previewError.IndexOf("80004002", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    PreviewPlaceholder.Text = "このファイル形式はプレビューできません";
                }
                else
                {
                    PreviewPlaceholder.Text = string.IsNullOrEmpty(previewError)
                        ? "このファイル形式に対応するプレビュー\n"
                          + "ハンドラがインストールされていません"
                        : $"プレビューの表示中にエラーが発生しました\n\n"
                          + "別のアプリでファイルを開いている場合は\n"
                          + "閉じてから再度お試しください。";
                }
                PreviewPlaceholder.Visibility = Visibility.Visible;
            }
        }

        private static readonly string[] ImageExtensions = {
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tiff", ".tif",
            ".ico", ".webp", ".wdp", ".hdp", ".jfif"
        };

        private static bool IsPowerPointFile(string filePath)
        {
            string ext = System.IO.Path.GetExtension(filePath)?.ToLowerInvariant();
            switch (ext)
            {
                case ".pptx": case ".pptm": case ".ppt":
                case ".ppsx": case ".ppsm": case ".pps":
                case ".potx": case ".potm": case ".pot":
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsPowerPointRunning()
        {
            // Only detect user-launched PowerPoint (has a visible main window).
            // COM-launched POWERPNT.EXE for preview handlers runs in the background
            // without a main window and should NOT be treated as a conflict.
            try
            {
                foreach (var proc in Process.GetProcessesByName("POWERPNT"))
                {
                    if (proc.MainWindowHandle != IntPtr.Zero)
                        return true;
                }
            }
            catch (Exception ex) { Debug.WriteLine($"IsPowerPointRunning check failed: {ex.Message}"); }
            return false;
        }

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
            catch (Exception ex)
            {
                Debug.WriteLine($"Image preview failed for {filePath}: {ex.Message}");
                PreviewPlaceholder.Text =
                    "この画像はプレビューできません\n\n"
                    + "ファイルが破損しているか、\n"
                    + "対応していない画像形式です。";
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
                catch (Exception ex)
                {
                    Debug.WriteLine($"PDF open failed for {filePath}: {ex.Message}");
                }

                if (docHandle == null || ct.IsCancellationRequested)
                {
                    docHandle?.Dispose();
                    if (!ct.IsCancellationRequested)
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            PreviewPlaceholder.Text =
                                "PDFを開けませんでした\n\n"
                                + "ファイルが破損しているか、\n"
                                + "パスワードで保護されている可能性があります。";
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

                    // Show ScrollViewer and force layout BEFORE building PDF pages.
                    // This ensures GetPdfPanelWidth() uses the actual ViewportWidth
                    // instead of a fallback estimate, preventing a double-layout flicker.
                    PdfScrollViewer.Visibility = Visibility.Visible;
                    PreviewPlaceholder.Visibility = Visibility.Collapsed;
                    PdfScrollViewer.UpdateLayout();

                    // Attach scroll event, then build layout and render
                    PdfScrollViewer.ScrollChanged -= PdfScrollViewer_ScrollChanged;
                    PdfScrollViewer.ScrollChanged += PdfScrollViewer_ScrollChanged;

                    RebuildPdfLayout();
                    RenderVisiblePdfPages();
                }));
            }, ct);
        }

        /// <summary>
        /// Calculates the available panel width for PDF pages.
        /// </summary>
        private double GetPdfPanelWidth()
        {
            // Use ActualWidth (not ViewportWidth) so the result is stable regardless
            // of whether the vertical scrollbar is visible. This prevents layout
            // oscillation where scrollbar appearance changes ViewportWidth → re-layout → flicker.
            double w = PdfScrollViewer.ActualWidth;
            if (w <= 0) w = PreviewHostBorder.ActualWidth;
            if (w <= 0) w = PreviewColumn.ActualWidth;
            if (w <= 0) w = 300;
            // Always reserve space for the vertical scrollbar to keep page width stable
            double panelWidth = w - SystemParameters.VerticalScrollBarWidth - 2;
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

            // Render at 1x physical pixel resolution for fast rendering and low memory usage
            var dpi = VisualTreeHelper.GetDpi(this);
            _pdfDocHandle.RenderWidth = (uint)(panelWidth * dpi.DpiScaleX);

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

            // Clean up completed tasks to prevent unbounded accumulation
            if (_pdfRenderTasks.Count > 20)
                _pdfRenderTasks.RemoveAll(t => t.IsCompleted);

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

                var renderTask = Task.Run(() =>
                {
                    if (ct.IsCancellationRequested) return;
                    System.Drawing.Bitmap bitmap = null;
                    try
                    {
                        bitmap = docHandle.RenderPage(pageIndex, ct);
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex) { Debug.WriteLine($"PDF page {pageIndex} render failed: {ex.Message}"); }

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
                    catch (Exception ex) { Debug.WriteLine($"PDF page {pageIndex} bitmap conversion failed: {ex.Message}"); }
                    finally
                    {
                        if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
                        bitmap.Dispose();
                    }
                });
                _pdfRenderTasks.Add(renderTask);
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

        /// <summary>
        /// Cancels PDF rendering and releases resources.
        /// When isClosing=true (app exit), skips COM Dispose to avoid STA/MTA
        /// cross-apartment deadlock — OS reclaims all COM objects on process exit.
        /// </summary>
        private void CancelPdfRendering(bool isClosing = false)
        {
            PdfScrollViewer.ScrollChanged -= PdfScrollViewer_ScrollChanged;

            if (_pdfResizeTimer != null)
            {
                _pdfResizeTimer.Stop();
            }

            if (_pdfRenderCts != null)
            {
                try { _pdfRenderCts.Cancel(); } catch { }
            }

            // Wait for background render tasks to finish.
            // With CancellationToken flowing through WaitForAsyncAction, tasks should
            // exit within ~20ms of cancellation. 3s timeout is a safety net.
            if (_pdfRenderTasks.Count > 0)
            {
                try { Task.WhenAll(_pdfRenderTasks).Wait(3000); }
                catch { }
                _pdfRenderTasks.Clear();
            }

            if (_pdfRenderCts != null)
            {
                try { _pdfRenderCts.Dispose(); } catch { }
                _pdfRenderCts = null;
            }

            if (_pdfDocHandle != null)
            {
                // Normal operation (preview switch): Dispose to free resources.
                // App exit: Skip Dispose to avoid COM cross-apartment deadlock.
                // OS reclaims all COM objects when the process terminates.
                if (!isClosing)
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
            catch (Exception ex)
            {
                Debug.WriteLine($"GetElementRectInWindow failed: {ex.Message}");
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

        /// <summary>
        /// Called before a file is opened (double-click / Enter / context menu).
        /// For PowerPoint files only: unloads the COM preview and terminates the
        /// background POWERPNT.EXE COM server. PowerPoint is single-instance,
        /// so the COM server process blocks normal file opening.
        /// Word/Excel previews are left intact (stream-based init avoids file locking).
        /// </summary>
        private void OnFileOpening()
        {
            if (_currentPreviewPath == null || !IsPowerPointFile(_currentPreviewPath))
                return;

            _previewHost?.UnloadPreview();
            _currentPreviewPath = null;

            // Terminate background POWERPNT.EXE (COM servers without visible windows).
            // User-launched PowerPoint (with main window) is left untouched.
            try
            {
                foreach (var proc in Process.GetProcessesByName("POWERPNT"))
                {
                    try
                    {
                        if (proc.MainWindowHandle == IntPtr.Zero)
                        {
                            proc.Kill();
                            proc.WaitForExit(500);
                        }
                    }
                    catch { }
                    finally { proc.Dispose(); }
                }
            }
            catch { }
        }

        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            // Save column order (quick, no COM involvement)
            try
            {
                var gridView = ResultsListView.View as GridView;
                if (gridView != null)
                {
                    var vm = DataContext as MainViewModel;
                    var order = gridView.Columns
                        .Select(c => c.Header as string)
                        .Where(h => h != null)
                        .Select(h => h.TrimEnd(' ', '▲', '▼'))
                        .ToList();
                    vm?.SaveColumnOrder(order);
                }
            }
            catch { }

            // Cancel PDF rendering signal only (don't wait for tasks)
            try { _pdfRenderCts?.Cancel(); } catch { }

            // Dispose ViewModel (cancels search, stops timers — no COM)
            // Run on thread pool with timeout to prevent deadlock
            try
            {
                var disposeTask = Task.Run(() =>
                {
                    try { (DataContext as MainViewModel)?.Dispose(); } catch { }
                });
                disposeTask.Wait(2000);
            }
            catch { }

            // Immediate process termination via TerminateProcess.
            // Unlike Environment.Exit(0), this does NOT run CLR shutdown or COM finalizers,
            // so it cannot deadlock on STA/MTA cross-apartment COM release.
            // OS reclaims all resources (COM objects, handles, memory) on process exit.
            Process.GetCurrentProcess().Kill();
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

            // Fallback for columns using CellTemplate instead of DisplayMemberBinding
            // (e.g., "名前" column with custom icon + highlighted text template)
            if (string.IsNullOrEmpty(sortBy))
            {
                string header = headerClicked.Column.Header as string;
                if (!string.IsNullOrEmpty(header))
                {
                    // Strip sort indicator (▲/▼) if present from previous sort
                    header = header.TrimEnd(' ', '▲', '▼');
                }
                switch (header)
                {
                    case "名前": sortBy = "Name"; break;
                    default: return; // Unknown column, skip
                }
            }

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

        /// <summary>
        /// Restores column order from saved settings.
        /// If settings are invalid or incomplete, keeps XAML-defined default order.
        /// </summary>
        private void RestoreColumnOrder(MainViewModel vm)
        {
            try
            {
                var settings = vm.GetSettings();
                if (settings?.ColumnOrder == null || settings.ColumnOrder.Count == 0)
                    return;

                var gridView = ResultsListView.View as GridView;
                if (gridView == null) return;

                var savedOrder = settings.ColumnOrder;

                // Validate: saved order must match current column count
                if (savedOrder.Count != gridView.Columns.Count)
                    return;

                // Build a lookup from header name to column
                var columnMap = new Dictionary<string, GridViewColumn>();
                foreach (var col in gridView.Columns)
                {
                    var header = col.Header as string;
                    if (header != null)
                        columnMap[header] = col;
                }

                // Validate: all saved header names must exist
                foreach (var name in savedOrder)
                {
                    if (!columnMap.ContainsKey(name))
                        return;
                }

                // Reorder columns to match saved order
                for (int i = 0; i < savedOrder.Count; i++)
                {
                    var targetCol = columnMap[savedOrder[i]];
                    int currentIndex = gridView.Columns.IndexOf(targetCol);
                    if (currentIndex != i)
                        gridView.Columns.Move(currentIndex, i);
                }
            }
            catch { }
        }
    }
}
