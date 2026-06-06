using System;
using System.Collections.Generic;
using System.IO;

namespace TNDexplorer.Core
{
    /// <summary>
    /// ファイルシステム上の1フォルダの直下の子要素（リコンサイル入力）。
    /// </summary>
    public class FsChild
    {
        public string Name;
        public bool IsDirectory;
        public long Size;
        public DateTime Modified;
    }

    /// <summary>
    /// ネットワークドライブ／カスタムフォルダの「変更が起きたフォルダ」をピンポイント再列挙し、
    /// FileIndex のそのフォルダ直下の子を差分反映する。
    /// I/O（ディレクトリ列挙）とFRN採番は外部から注入し、本クラスはインデックス更新ロジックに専念する。
    /// 1インスタンス=1フラッシュバッチ（使い捨て）。スレッド非安全（呼び出し側で直列化すること）。
    /// </summary>
    public class NetworkIndexReconciler
    {
        private readonly FileIndex _index;
        private readonly byte _driveIndex;
        private readonly string _watchRoot;          // 末尾に "\" を付けて正規化済み
        private readonly Func<string, List<FsChild>> _listChildren;  // null=フォルダ消失
        private readonly Func<ulong> _allocateFrn;

        // スナップショットから構築するマップ（バッチ中、自前で同期更新する）
        private Dictionary<ulong, FileEntry> _byFrn;
        // parentFrn -> (子名(小文字) -> エントリ)
        private Dictionary<ulong, Dictionary<string, FileEntry>> _childrenByParent;
        // 相対フォルダパス(小文字, バックスラッシュ区切り, 先頭末尾区切りなし) -> dir FRN。"" はルート(=FRN 0)。
        private Dictionary<string, ulong> _dirFrnCache;
        // 1バッチ内の listChildren 結果キャッシュ（フルパス小文字 -> 結果 or null）
        private Dictionary<string, List<FsChild>> _listCache;

        public NetworkIndexReconciler(
            FileIndex index,
            byte driveIndex,
            string watchRoot,
            Func<string, List<FsChild>> listChildren,
            Func<ulong> allocateFrn)
        {
            _index = index;
            _driveIndex = driveIndex;
            _watchRoot = watchRoot.EndsWith("\\") ? watchRoot : watchRoot + "\\";
            _listChildren = listChildren;
            _allocateFrn = allocateFrn;
        }

        /// <summary>
        /// dirtyフォルダ群（フルパス）を反映する。浅い順（パス区切り数昇順）に処理することで、
        /// 親フォルダが子より先に作成されることを保証する。
        /// </summary>
        public void ReconcileBatch(IEnumerable<string> dirFullPaths)
        {
            BuildSnapshot();
            _listCache = new Dictionary<string, List<FsChild>>(StringComparer.OrdinalIgnoreCase);

            var work = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in dirFullPaths)
            {
                string norm = d.TrimEnd('\\');
                if (seen.Add(norm)) work.Add(norm);
            }
            work.Sort((a, b) => CountSep(a).CompareTo(CountSep(b)));

            var queue = new Queue<string>(work);
            var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            while (queue.Count > 0)
            {
                string dir = queue.Dequeue();
                if (!processed.Add(dir)) continue;

                ulong? frn = EnsureDirFrn(dir);

                var children = ListCached(dir);
                if (children == null)
                {
                    // フォルダがFS上に存在しない → インデックスにあればサブツリー削除
                    if (frn.HasValue && frn.Value != 0)
                        RemoveSubtree(frn.Value);
                    continue;
                }

                if (!frn.HasValue) continue; // 解決不能（親が消えている等）
                ReconcileChildren(dir, frn.Value, children, queue);
            }
        }

        private void BuildSnapshot()
        {
            var entries = _index.GetEntriesForDrive(_driveIndex);
            _byFrn = new Dictionary<ulong, FileEntry>(entries.Count);
            _childrenByParent = new Dictionary<ulong, Dictionary<string, FileEntry>>();
            _dirFrnCache = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase) { [""] = 0 };

            foreach (var e in entries)
            {
                _byFrn[e.FileReferenceNumber] = e;
                AddToChildren(e);
            }
        }

        private void AddToChildren(FileEntry e)
        {
            if (!_childrenByParent.TryGetValue(e.ParentFileReferenceNumber, out var dict))
            {
                dict = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
                _childrenByParent[e.ParentFileReferenceNumber] = dict;
            }
            dict[e.Name] = e;
        }

        private void RemoveFromChildren(FileEntry e)
        {
            if (_childrenByParent.TryGetValue(e.ParentFileReferenceNumber, out var dict))
                dict.Remove(e.Name);
        }

        /// <summary>
        /// フォルダのフルパス -> dir FRN。インデックスに無い新規フォルダは、親を辿りFSに実在すれば作成する。
        /// ルートは 0。解決不能（FSに存在しない等）は null。
        /// </summary>
        private ulong? EnsureDirFrn(string dirFullPath)
        {
            string rel = ToRelative(dirFullPath);
            if (rel.Length == 0) return 0; // ルート

            if (_dirFrnCache.TryGetValue(rel, out ulong known))
                return known;

            string parentPath = ParentPath(dirFullPath);
            ulong? parentFrn = EnsureDirFrn(parentPath);
            if (!parentFrn.HasValue) return null;

            string name = NameOf(dirFullPath);

            // 既存インデックスの親の子に同名があれば確認
            if (_childrenByParent.TryGetValue(parentFrn.Value, out var siblings)
                && siblings.TryGetValue(name, out var existing))
            {
                if (existing.IsDirectory)
                {
                    _dirFrnCache[rel] = existing.FileReferenceNumber;
                    return existing.FileReferenceNumber;
                }
                // 同名だがファイル → 種別変更。旧ファイルエントリを除去し、下でフォルダとして作り直す。
                RemoveLeaf(existing);
            }

            // インデックスに無い → FS上に実在するか親を列挙して確認し、あれば作成
            var parentChildren = ListCached(parentPath);
            if (parentChildren == null) return null;

            FsChild match = null;
            foreach (var c in parentChildren)
            {
                if (c.IsDirectory && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                { match = c; break; }
            }
            if (match == null) return null; // FSにも無い

            ulong frn = AddEntry(parentFrn.Value, match);
            _dirFrnCache[rel] = frn;
            return frn;
        }

        private void ReconcileChildren(string dirFullPath, ulong dirFrn, List<FsChild> fsChildren, Queue<string> queue)
        {
            _childrenByParent.TryGetValue(dirFrn, out var idxChildren);
            var fsByName = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 追加・更新
            foreach (var fc in fsChildren)
            {
                fsByName.Add(fc.Name);

                FileEntry existing = null;
                if (idxChildren != null) idxChildren.TryGetValue(fc.Name, out existing);

                if (existing != null)
                {
                    if (existing.IsDirectory != fc.IsDirectory)
                    {
                        // 同名で種別が変わった（ファイル⇔フォルダ。エディタのアトミックリネーム等）:
                        // 旧エントリを削除して新規追加する。
                        if (existing.IsDirectory) RemoveSubtree(existing.FileReferenceNumber);
                        else RemoveLeaf(existing);

                        AddEntry(dirFrn, fc);
                        if (fc.IsDirectory)
                            queue.Enqueue(Path.Combine(dirFullPath, fc.Name));
                    }
                    else if (!fc.IsDirectory)
                    {
                        // ファイル更新: サイズ/更新日時が変わっていれば反映。
                        // 並行検索がこのFileEntryのフィールドをロックなしで読むため、インプレース変更は
                        // x86でDateTime/longのtorn readハザードになる。参照差し替え(UpdateEntry)で回避する。
                        if (existing.Size != fc.Size || existing.ModifiedDate != fc.Modified)
                            UpdateFile(dirFrn, existing, fc);
                    }
                    // フォルダ（種別一致）の中身は、そのフォルダがdirtyなら別途処理される
                }
                else
                {
                    AddEntry(dirFrn, fc);
                    if (fc.IsDirectory)
                    {
                        // 新規サブフォルダ → 中身を拾うためキューに追加（再帰的に展開される）
                        queue.Enqueue(Path.Combine(dirFullPath, fc.Name));
                    }
                }
            }

            // 削除（インデックスにあるがFSに無い）
            if (idxChildren != null)
            {
                var toRemove = new List<FileEntry>();
                foreach (var kvp in idxChildren)
                {
                    if (!fsByName.Contains(kvp.Key))
                        toRemove.Add(kvp.Value);
                }
                foreach (var e in toRemove)
                {
                    if (e.IsDirectory) RemoveSubtree(e.FileReferenceNumber);
                    else RemoveLeaf(e);
                }
            }
        }

        private ulong AddEntry(ulong parentFrn, FsChild fc)
        {
            ulong frn = _allocateFrn();
            var flags = FileEntryFlags.Network;
            if (fc.IsDirectory) flags |= FileEntryFlags.Directory;

            var entry = new FileEntry
            {
                FileReferenceNumber = frn,
                ParentFileReferenceNumber = parentFrn,
                Name = fc.Name,
                Size = fc.Size,
                ModifiedDate = fc.Modified,
                Flags = flags,
                DriveIndex = _driveIndex,
            };

            _index.AddEntry(_driveIndex, entry);
            _byFrn[frn] = entry;
            AddToChildren(entry);
            if (fc.IsDirectory && !_childrenByParent.ContainsKey(frn))
                _childrenByParent[frn] = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);

            return frn;
        }

        private void UpdateFile(ulong parentFrn, FileEntry existing, FsChild fc)
        {
            // 既存FRN/親/名前/フラグは保持し、サイズと更新日時だけ更新した新インスタンスに差し替える。
            var updated = new FileEntry
            {
                FileReferenceNumber = existing.FileReferenceNumber,
                ParentFileReferenceNumber = existing.ParentFileReferenceNumber,
                Name = existing.Name,
                Size = fc.Size,
                ModifiedDate = fc.Modified,
                Flags = existing.Flags,
                DriveIndex = _driveIndex,
            };

            _index.UpdateEntry(_driveIndex, existing.FileReferenceNumber, updated);
            _byFrn[existing.FileReferenceNumber] = updated;
            if (_childrenByParent.TryGetValue(parentFrn, out var dict))
                dict[existing.Name] = updated;
        }

        private void RemoveLeaf(FileEntry e)
        {
            _index.RemoveEntry(_driveIndex, e.FileReferenceNumber);
            _byFrn.Remove(e.FileReferenceNumber);
            RemoveFromChildren(e);
        }

        private void RemoveSubtree(ulong rootFrn)
        {
            // BFSで子孫を収集して削除
            var stack = new Stack<ulong>();
            stack.Push(rootFrn);
            var toDelete = new List<FileEntry>();

            while (stack.Count > 0)
            {
                ulong frn = stack.Pop();
                if (!_byFrn.TryGetValue(frn, out var entry)) continue;
                toDelete.Add(entry);

                if (_childrenByParent.TryGetValue(frn, out var children))
                {
                    foreach (var child in children.Values)
                        stack.Push(child.FileReferenceNumber);
                }
            }

            foreach (var e in toDelete)
            {
                _index.RemoveEntry(_driveIndex, e.FileReferenceNumber);
                _byFrn.Remove(e.FileReferenceNumber);
                RemoveFromChildren(e);
                _childrenByParent.Remove(e.FileReferenceNumber);
            }
        }

        private List<FsChild> ListCached(string dirFullPath)
        {
            if (_listCache.TryGetValue(dirFullPath, out var cached))
                return cached;
            var result = _listChildren(dirFullPath);
            _listCache[dirFullPath] = result;
            return result;
        }

        private string ToRelative(string fullPath)
        {
            string p = fullPath.TrimEnd('\\');
            string root = _watchRoot.TrimEnd('\\');
            if (string.Equals(p, root, StringComparison.OrdinalIgnoreCase))
                return "";
            if (p.StartsWith(_watchRoot, StringComparison.OrdinalIgnoreCase))
                return p.Substring(_watchRoot.Length);
            // 想定外（watchRoot 配下でない）→ ルート扱い
            return "";
        }

        private static string ParentPath(string fullPath)
        {
            string p = fullPath.TrimEnd('\\');
            int idx = p.LastIndexOf('\\');
            return idx > 0 ? p.Substring(0, idx) : p;
        }

        private static string NameOf(string fullPath)
        {
            string p = fullPath.TrimEnd('\\');
            int idx = p.LastIndexOf('\\');
            return idx >= 0 ? p.Substring(idx + 1) : p;
        }

        private static int CountSep(string s)
        {
            int n = 0;
            for (int i = 0; i < s.Length; i++) if (s[i] == '\\') n++;
            return n;
        }
    }
}
