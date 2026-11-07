using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ChikumaTateshina.UnityAssetsOptimizer
{
    public sealed class RunRecord
    {
        public RunManifest Manifest;
        public string RunFolder;
        public string Error;
    }

    public enum OriginalState { Unchanged, ModifiedRestorable, ModifiedUnrecoverable, Missing }

    public sealed class RestoreCheckEntry
    {
        public ManifestAsset Asset;
        public OriginalState State;
        public string Detail;
    }

    /// <summary>
    /// 復元処理。基本は「生成物の破棄」。原本が外部要因で変更されていた場合のみ、検証済みスナップショットから書き戻す。
    /// Unity の Undo には依存しない。
    /// </summary>
    public static class RestoreService
    {
        public static List<RunRecord> FindRuns()
        {
            var list = new List<RunRecord>();
            var rootFull = Path.Combine(UnityAssetsOptimizerInfo.ProjectRoot, UnityAssetsOptimizerInfo.GeneratedRoot);
            if (!Directory.Exists(rootFull)) return list;
            foreach (var dir in Directory.GetDirectories(rootFull).OrderByDescending(d => d, StringComparer.Ordinal))
            {
                var manifestPath = Path.Combine(dir, UnityAssetsOptimizerInfo.ManifestFileName);
                if (!File.Exists(manifestPath)) continue;
                var record = new RunRecord { RunFolder = UnityAssetsOptimizerInfo.GeneratedRoot + "/" + Path.GetFileName(dir) };
                try
                {
                    record.Manifest = JsonUtility.FromJson<RunManifest>(File.ReadAllText(manifestPath));
                    if (record.Manifest == null || record.Manifest.schemaVersion != UnityAssetsOptimizerInfo.ManifestSchemaVersion)
                        record.Error = "Manifest のバージョンが不明です。";
                }
                catch (Exception e)
                {
                    record.Error = "Manifest が破損しています: " + e.Message;
                }
                list.Add(record);
            }
            return list;
        }

        public static List<RestoreCheckEntry> CheckOriginals(RunManifest manifest)
        {
            var result = new List<RestoreCheckEntry>();
            var copyErrors = new HashSet<string>(SnapshotService.VerifyCopies(manifest.runId, manifest.snapshotAssets).Select(e => e.Split(':')[0]));
            foreach (var a in manifest.snapshotAssets)
            {
                var entry = new RestoreCheckEntry { Asset = a };
                var full = AssetPathUtility.ToFullPath(a.path);
                bool exists = full != null && File.Exists(full);
                bool same = exists && HashUtility.Sha256File(full, false) == a.sha256 &&
                            (a.metaSha256 == null || HashUtility.Sha256File(full + ".meta", false) == a.metaSha256);
                bool snapshotOk = a.snapshotted && !copyErrors.Contains(a.path);
                if (same) entry.State = OriginalState.Unchanged;
                else if (!snapshotOk) entry.State = OriginalState.ModifiedUnrecoverable;
                else if (!exists) entry.State = OriginalState.Missing;
                else entry.State = OriginalState.ModifiedRestorable;

                if (entry.State != OriginalState.Unchanged && !a.path.StartsWith("Assets/", StringComparison.Ordinal))
                {
                    entry.State = OriginalState.ModifiedUnrecoverable;
                    entry.Detail = "Assets 外（パッケージ等）は書き戻しません";
                }
                else if (entry.State == OriginalState.ModifiedUnrecoverable && entry.Detail == null)
                {
                    entry.Detail = "スナップショットが欠落/破損しています";
                }
                result.Add(entry);
            }
            return result;
        }

        /// <summary>実行フォルダの外（指定された出力先）に置いた生成物。GUID が記録と一致するものだけを返す。</summary>
        public static List<string> ExternalOutputs(RunManifest manifest, string runFolder)
        {
            if (manifest == null) return new List<string>();
            return manifest.outputs
                .Where(o => !AssetPathUtility.IsUnder(o.path, runFolder) && AssetPathUtility.IsUnder(o.path, "Assets"))
                .Where(o => !string.IsNullOrEmpty(o.guid) && AssetDatabase.GUIDToAssetPath(o.guid) == o.path)
                .Select(o => o.path).ToList();
        }

        /// <summary>読み込み中のシーンから生成物への参照を探す（手動配置の検出）。</summary>
        public static List<string> FindSceneReferences(string runFolder, RunManifest manifest = null)
        {
            var external = new HashSet<string>(ExternalOutputs(manifest, runFolder));
            var refs = new List<string>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                {
                    var deps = EditorUtility.CollectDependencies(new UnityEngine.Object[] { root });
                    foreach (var d in deps)
                    {
                        if (d == null || !EditorUtility.IsPersistent(d)) continue;
                        var p = AssetDatabase.GetAssetPath(d);
                        if (AssetPathUtility.IsUnder(p, runFolder) || external.Contains(p)) refs.Add(scene.path + ":" + root.name + " → " + p);
                    }
                }
            }
            return refs.Distinct().ToList();
        }

        /// <summary>
        /// 生成物（とその実行のスナップショット）を削除する。原本には触れない。
        /// manifest を渡すと、出力先フォルダへ移した生成物（GUID が一致するもの）と、空になった作成フォルダも削除する。
        /// </summary>
        public static bool DiscardRun(string runFolder, out string error, RunManifest manifest = null)
        {
            error = null;
            if (!AssetPathUtility.IsUnder(runFolder, UnityAssetsOptimizerInfo.GeneratedRoot) || runFolder == UnityAssetsOptimizerInfo.GeneratedRoot)
            {
                error = "管理領域外のフォルダは削除しません: " + runFolder;
                return false;
            }
            foreach (var p in ExternalOutputs(manifest, runFolder)) AssetDatabase.DeleteAsset(p);
            if (manifest != null) AssetPathUtility.DeleteEmptyFolders(manifest.createdFolders);
            if (AssetDatabase.IsValidFolder(runFolder) && !AssetDatabase.DeleteAsset(runFolder))
            {
                error = "削除に失敗しました: " + runFolder;
                return false;
            }
            var full = Path.Combine(UnityAssetsOptimizerInfo.ProjectRoot, runFolder);
            if (Directory.Exists(full)) Directory.Delete(full, true);
            if (File.Exists(full + ".meta")) File.Delete(full + ".meta");
            AssetDatabase.Refresh();
            return true;
        }

        /// <summary>変更/欠落した原本を、検証済みスナップショットから書き戻す（GUID を保つため .meta も戻す）。</summary>
        public static List<string> RestoreOriginals(RunManifest manifest, IEnumerable<RestoreCheckEntry> entries)
        {
            var messages = new List<string>();
            var dir = UnityAssetsOptimizerInfo.SnapshotFolderFull(manifest.runId);
            var restored = new List<string>();
            foreach (var e in entries)
            {
                if (e.State != OriginalState.ModifiedRestorable && e.State != OriginalState.Missing) continue;
                var a = e.Asset;
                var full = AssetPathUtility.ToFullPath(a.path);
                if (full == null || !a.path.StartsWith("Assets/", StringComparison.Ordinal))
                {
                    messages.Add(a.path + ": 書き戻し先を解決できません（失敗）");
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(full));
                File.Copy(Path.Combine(dir, a.snapshotFile), full, true);
                if (!string.IsNullOrEmpty(a.snapshotMetaFile)) File.Copy(Path.Combine(dir, a.snapshotMetaFile), full + ".meta", true);
                restored.Add(a.path);
            }
            foreach (var p in restored) AssetDatabase.ImportAsset(p, ImportAssetOptions.ForceUpdate);

            foreach (var p in restored)
            {
                var a = manifest.snapshotAssets.First(x => x.path == p);
                var ok = HashUtility.Sha256File(AssetPathUtility.ToFullPath(p), false) == a.sha256 && AssetDatabase.AssetPathToGUID(p) == a.guid;
                messages.Add(p + (ok ? ": 復元しました（内容・GUID 一致）" : ": 書き戻しましたが検証に失敗しました"));
            }
            return messages;
        }
    }
}
