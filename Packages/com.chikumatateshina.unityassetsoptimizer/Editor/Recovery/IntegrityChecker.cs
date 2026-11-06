using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ChikumaTateshina.UnityAssetsOptimizer
{
    [Serializable]
    public class IntegrityReport
    {
        public bool passed = true;
        public List<string> errors = new List<string>();
        public List<string> warnings = new List<string>();
        public int checkedAssets;
    }

    /// <summary>原本のバイト列・GUID・階層構成・シーン状態が処理前と同一であることを検証する。</summary>
    public static class IntegrityChecker
    {
        public static IntegrityReport Check(IEnumerable<ManifestAsset> assets, IList<HierarchyRecord> hierarchy, IList<GameObject> roots, IList<SceneState> sceneStates)
        {
            var report = new IntegrityReport();
            foreach (var a in assets)
            {
                report.checkedAssets++;
                var full = AssetPathUtility.ToFullPath(a.path);
                if (full == null || !File.Exists(full))
                {
                    report.errors.Add(a.path + ": 原本が見つかりません");
                    continue;
                }
                if (a.sha256 != null && HashUtility.Sha256File(full, false) != a.sha256) report.errors.Add(a.path + ": 原本の内容が変化しました");
                if (a.metaSha256 != null && HashUtility.Sha256File(full + ".meta", false) != a.metaSha256) report.errors.Add(a.path + ": 原本の .meta が変化しました");
                if (AssetDatabase.AssetPathToGUID(a.path) != a.guid) report.errors.Add(a.path + ": GUID が変化しました");
            }

            if (hierarchy != null && roots != null)
            {
                for (int i = 0; i < roots.Count && i < hierarchy.Count; i++)
                {
                    if (roots[i] == null)
                    {
                        report.errors.Add(hierarchy[i].root + ": 原本オブジェクトが失われました");
                        continue;
                    }
                    var now = SnapshotService.RecordHierarchy(roots[i]);
                    if (!now.entries.SequenceEqual(hierarchy[i].entries)) report.errors.Add(hierarchy[i].root + ": 原本の階層/コンポーネント構成が変化しました");
                }
            }

            if (sceneStates != null)
            {
                var current = SnapshotService.CaptureSceneStates();
                foreach (var before in sceneStates)
                {
                    var after = current.FirstOrDefault(s => s.path == before.path);
                    if (after == null) continue;
                    if (!before.wasDirty && after.wasDirty)
                        report.warnings.Add(before.path + ": シーンが dirty になりました（保存はしていません。保存せずに閉じれば元の状態です）");
                }
            }

            report.passed = report.errors.Count == 0;
            return report;
        }
    }
}
