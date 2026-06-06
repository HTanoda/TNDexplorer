using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace TNDexplorer.Core
{
    /// <summary>
    /// 変更監視対象（1ドライブ＝1Watcher）。
    /// </summary>
    public class NetworkChangeTarget
    {
        public byte DriveIndex;
        public string WatchRootPath; // FileSystemWatcher が監視する起点（"K:\\" or UNC）
    }

    /// <summary>
    /// ネットワークドライブ／カスタムフォルダを FileSystemWatcher で再帰監視し、
    /// 変更が起きたフォルダのフルパスをデバウンスしてリコンサイルコールバックに渡す。
    /// バッファ溢れ等の取りこぼし時はフルスキャンコールバックを呼ぶ。
    /// SMBアクセスのため、Watcher のハンドルを開く(EnableRaisingEvents=true)瞬間は
    /// 呼び出し側が渡す impersonate デリゲートの中で実行する。
    /// </summary>
    public class NetworkChangeMonitor : IDisposable
    {
        private readonly IReadOnlyList<NetworkChangeTarget> _targets;
        private readonly Action<byte, IReadOnlyCollection<string>> _reconcileBatch;
        private readonly Action<byte> _requestFullRescan;
        private readonly Action<Action> _impersonate; // 例: NetworkDriveDetector.RunImpersonated
        private readonly int _debounceMs;

        private readonly List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();
        // dirtyフォルダ: キー "driveIndex|dirPathLower" -> (driveIndex, dirPath)
        private readonly object _dirtyLock = new object();
        private Dictionary<string, KeyValuePair<byte, string>> _dirty
            = new Dictionary<string, KeyValuePair<byte, string>>();

        private Timer _flushTimer;
        private int _flushing; // 0/1 リエントランシガード
        private bool _disposed;

        public NetworkChangeMonitor(
            IReadOnlyList<NetworkChangeTarget> targets,
            Action<byte, IReadOnlyCollection<string>> reconcileBatch,
            Action<byte> requestFullRescan,
            Action<Action> impersonate,
            int debounceMs = 3000)
        {
            _targets = targets;
            _reconcileBatch = reconcileBatch;
            _requestFullRescan = requestFullRescan;
            _impersonate = impersonate;
            _debounceMs = debounceMs;
        }

        public void Start()
        {
            foreach (var t in _targets)
            {
                try
                {
                    CreateWatcher(t);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"NetworkChangeMonitor: Watcher作成失敗 drive={t.DriveIndex} path={t.WatchRootPath}: {ex.Message}");
                    // この対象は監視なし（定期スキャンにフォールバック）
                }
            }

            _flushTimer = new Timer(OnFlush, null, _debounceMs, _debounceMs);
        }

        private void CreateWatcher(NetworkChangeTarget t)
        {
            FileSystemWatcher w = null;
            _impersonate(() =>
            {
                w = new FileSystemWatcher(t.WatchRootPath)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                                 | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                };

                byte di = t.DriveIndex;
                w.Created += (s, e) => OnChanged(di, e.FullPath);
                w.Deleted += (s, e) => OnChanged(di, e.FullPath);
                w.Changed += (s, e) => OnChanged(di, e.FullPath);
                w.Renamed += (s, e) =>
                {
                    OnChanged(di, e.FullPath);
                    OnChanged(di, e.OldFullPath);
                };
                w.Error += (s, e) => OnError(di, t, e);

                // ハンドルを開く（インピーソネーション下）
                w.EnableRaisingEvents = true;
            });

            if (w != null)
                lock (_watchers) { _watchers.Add(w); }
        }

        private void OnChanged(byte driveIndex, string fullPath)
        {
            if (_disposed) return;
            // 変更が起きた「フォルダ」= 変更パスの親ディレクトリ
            string dir;
            try { dir = Path.GetDirectoryName(fullPath); }
            catch { return; }
            if (string.IsNullOrEmpty(dir)) return;

            string key = driveIndex + "|" + dir.ToLowerInvariant();
            lock (_dirtyLock)
            {
                _dirty[key] = new KeyValuePair<byte, string>(driveIndex, dir);
            }
        }

        private void OnError(byte driveIndex, NetworkChangeTarget t, ErrorEventArgs e)
        {
            Debug.WriteLine($"NetworkChangeMonitor: Error drive={driveIndex}: {e.GetException()?.Message}");
            // 取りこぼし（バッファ溢れ等）→ その対象をフルスキャンで回収
            try { _requestFullRescan(driveIndex); } catch { }

            // Watcher を作り直す
            try
            {
                _impersonate(() =>
                {
                    try { CreateWatcher(t); } catch { }
                });
            }
            catch { }
        }

        private void OnFlush(object state)
        {
            if (_disposed) return;
            if (Interlocked.CompareExchange(ref _flushing, 1, 0) != 0) return; // 前回フラッシュ実行中
            try
            {
                Dictionary<string, KeyValuePair<byte, string>> snapshot;
                lock (_dirtyLock)
                {
                    if (_dirty.Count == 0) return;
                    snapshot = _dirty;
                    _dirty = new Dictionary<string, KeyValuePair<byte, string>>();
                }

                // ドライブごとにグルーピング
                var byDrive = new Dictionary<byte, List<string>>();
                foreach (var kvp in snapshot.Values)
                {
                    if (!byDrive.TryGetValue(kvp.Key, out var list))
                    {
                        list = new List<string>();
                        byDrive[kvp.Key] = list;
                    }
                    list.Add(kvp.Value);
                }

                foreach (var kvp in byDrive)
                {
                    try { _reconcileBatch(kvp.Key, kvp.Value); }
                    catch (Exception ex) { Debug.WriteLine($"NetworkChangeMonitor: reconcile失敗 drive={kvp.Key}: {ex.Message}"); }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _flushing, 0);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _flushTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _flushTimer?.Dispose();

            lock (_watchers)
            {
                foreach (var w in _watchers)
                {
                    try { w.EnableRaisingEvents = false; w.Dispose(); } catch { }
                }
                _watchers.Clear();
            }
        }
    }
}
