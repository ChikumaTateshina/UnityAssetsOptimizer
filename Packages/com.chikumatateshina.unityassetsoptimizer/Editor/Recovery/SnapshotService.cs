using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ChikumaTateshina.UnityAssetsOptimizer
{
    [Serializable]
    public class HierarchyRecord
    {
        public string root;
        public List<string> entries = new List<string>();
    }

    [Serializable]
    public class SnapshotIndex
    {
        public int schemaVersion = 1;
        public string runId;
        public List<ManifestAsset> assets = new List<ManifestAsset>();
        public List<HierarchyRecord> hierarchy = new List<HierarchyRecord>();
    }

    public sealed class SnapshotResult
    {
        public bool Success;
        public string SnapshotHash;
        public readonly List<ManifestAsset> Assets = new List<ManifestAsset>();
        public readonly List<HierarchyRecord> Hierarchy = new List<HierarchyRecord>();
        public readonly List<string> Errors = new List<string>();
    }

    /// <summary>
    /// 原本アセットの独立コピーと識別情報（GUID・内容ハッシュ・階層構成）を記録する。
    /// 保存先は "Snapshot~"（Unity がインポートしないフォルダ）。
    /// </summary>
    public static class SnapshotService
    {
        public static long EstimateSize(IEnumerable<string> assetPaths)
        {
            long total = 0;
            foreach (var p in assetPaths)
            {
                var full = AssetPathUtility.ToFullPath(p);
                if (full == null) continue;
                if (File.Exists(full)) total += new FileInfo(full).Length;
                if (File.Exists(full + ".meta")) total += new FileInfo(full + ".meta").Length;
            }
            return total;
        }

        public static SnapshotResult Create(string runId, IEnumerable<string> assetPaths, IList<GameObject> roots, Func<bool> cancel)
        {
            var result = new SnapshotResult();
            var dir = UnityAssetsOptimizerInfo.SnapshotFolderFull(runId);
            var filesDir = Path.Combine(dir, "files");
            Directory.CreateDirectory(filesDir);

            int index = 0;
            foreach (var path in assetPaths.OrderBy(p => p, StringComparer.Ordinal))
            {
                if (cancel != null && cancel()) throw new OperationCanceledException("ユーザーによりキャンセルされました。");
                var entry = new ManifestAsset { path = path, guid = AssetDatabase.AssetPathToGUID(path) };
                result.Assets.Add(entry);
                var full = AssetPathUtility.ToFullPath(path);
                if (full == null || !File.Exists(full))
                {
                    entry.note = "ファイル実体を解決できません";
                    result.Errors.Add(path + ": " + entry.note);
                    continue;
                }

                var name = (index++).ToString("D5") + "_" + AssetPathUtility.SanitizeFileName(Path.GetFileName(path));
                entry.snapshotFile = "files/" + name;
                entry.sha256 = HashUtility.Sha256File(full, false);
                entry.size = new FileInfo(full).Length;
                File.Copy(full, Path.Combine(filesDir, name), false);
                if (HashUtility.Sha256File(Path.Combine(filesDir, name), false) != entry.sha256)
                {
                    result.Errors.Add(path + ": スナップショットのコピーが原本と一致しません");
                    continue;
                }

                var meta = full + ".meta";
                if (File.Exists(meta))
                {
                    entry.snapshotMetaFile = entry.snapshotFile + ".meta";
                    entry.metaSha256 = HashUtility.Sha256File(meta, false);
                    File.Copy(meta, Path.Combine(filesDir, name + ".meta"), false);
                    if (HashUtility.Sha256File(Path.Combine(filesDir, name + ".meta"), false) != entry.metaSha256)
                    {
                        result.Errors.Add(path + ": .meta のコピーが原本と一致しません");
                        continue;
                    }
                }
                entry.snapshotted = true;
            }

            foreach (var root in roots) result.Hierarchy.Add(RecordHierarchy(root));

            var index2 = new SnapshotIndex { runId = runId };
            index2.assets.AddRange(result.Assets);
            index2.hierarchy.AddRange(result.Hierarchy);
            File.WriteAllText(Path.Combine(dir, "snapshot.json"), JsonUtility.ToJson(index2, true), new UTF8Encoding(false));

            result.SnapshotHash = ComputeSnapshotHash(result.Assets, result.Hierarchy);
            result.Success = result.Errors.Count == 0;
            return result;
        }

        public static string ComputeSnapshotHash(IEnumerable<ManifestAsset> assets, IEnumerable<HierarchyRecord> hierarchy)
        {
            var sb = new StringBuilder();
            foreach (var a in assets) sb.Append(a.path).Append('|').Append(a.guid).Append('|').Append(a.sha256).Append('|').Append(a.metaSha256).Append('\n');
            foreach (var h in hierarchy) sb.Append(h.root).Append('\n').Append(string.Join("\n", h.entries)).Append('\n');
            return HashUtility.Sha256(sb.ToString());
        }

        /// <summary>階層・コンポーネント構成の記録（変化検出用）。</summary>
        public static HierarchyRecord RecordHierarchy(GameObject root)
        {
            var record = new HierarchyRecord { root = AssetPathUtility.DisplayPath(root) };
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                var types = t.GetComponents<Component>().Select(c => c == null ? "(missing)" : c.GetType().FullName);
                record.entries.Add(AssetPathUtility.HierarchyPath(t, root.transform) + " :: " + t.gameObject.activeSelf + " :: " + string.Join(",", types));
            }
            return record;
        }

        /// <summary>スナップショットのコピー自体が破損していないか検証する。</summary>
        public static List<string> VerifyCopies(string runId, IEnumerable<ManifestAsset> assets)
        {
            var errors = new List<string>();
            var dir = UnityAssetsOptimizerInfo.SnapshotFolderFull(runId);
            foreach (var a in assets)
            {
                if (!a.snapshotted)
                {
                    errors.Add(a.path + ": スナップショットがありません（" + a.note + "）");
                    continue;
                }
                var copy = Path.Combine(dir, a.snapshotFile);
                if (HashUtility.Sha256File(copy, false) != a.sha256) errors.Add(a.path + ": スナップショットが破損/欠落しています");
                if (!string.IsNullOrEmpty(a.snapshotMetaFile) && HashUtility.Sha256File(Path.Combine(dir, a.snapshotMetaFile), false) != a.metaSha256)
                    errors.Add(a.path + ": スナップショットの .meta が破損/欠落しています");
            }
            return errors;
        }

        public static List<SceneState> CaptureSceneStates()
        {
            var list = new List<SceneState>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                list.Add(new SceneState { path = s.path, guid = AssetDatabase.AssetPathToGUID(s.path), isLoaded = s.isLoaded, wasDirty = s.isDirty });
            }
            var stage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null)
                list.Add(new SceneState { path = "PrefabStage:" + stage.assetPath, guid = AssetDatabase.AssetPathToGUID(stage.assetPath), isLoaded = true, wasDirty = stage.scene.isDirty });
            return list;
        }
    }
}
