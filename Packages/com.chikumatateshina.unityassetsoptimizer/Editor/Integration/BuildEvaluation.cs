using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ChikumaTateshina.UnityAssetsOptimizer
{
    /// <summary>アップロード時と同じ（NDMF 適用後の）構成での評価結果。</summary>
    public sealed class BuildEvaluation
    {
        public string Label;
        public MetricSet Metrics;
        public List<RankResult> Ranks = new List<RankResult>();
        /// <summary>NDMF 適用後の構成の AssetBundle サイズ（計測した場合のみ）。</summary>
        public BundleSize Size;
        public string Error;
    }

    /// <summary>AssetBundle としてのファイルサイズ（VRChat の表示と同じ「圧縮後」と「展開後」）。</summary>
    public sealed class BundleSize
    {
        public string Label;
        public long Compressed;
        public long Uncompressed;
        public string Error;
    }

    public static class BuildEvaluator
    {
        /// <summary>
        /// root の複製に NDMF のビルド処理（Modular Avatar・AAO・lilNDMFMeshSimplifier 等）を適用して計測・評価する。
        /// 複製はプレビューシーン上に作り、NDMF の生成物は一時フォルダに保存して評価後に削除するため、原本・シーンは変更されない。
        /// measureSize なら処理後の構成を一時 Prefab にして AssetBundle のサイズ（アップロード時のサイズ）も測る。
        /// </summary>
        public static BuildEvaluation Evaluate(GameObject root, string label, bool measureSize = false)
        {
            var result = new BuildEvaluation { Label = label };
            if (root == null)
            {
                result.Error = "対象がありません。";
                return result;
            }
            if (!NdmfBuildEvaluator.IsAvailable(out var reason))
            {
                result.Error = reason;
                return result;
            }
            var created = new List<string>();
            string temp = null;
            try
            {
                AssetPathUtility.EnsureFolderTracked(MeasureFolder, created);
                temp = AssetDatabase.GenerateUniqueAssetPath(MeasureFolder + "/ndmf");
                using (var copy = NdmfBuildEvaluator.ProcessCopy(root, temp))
                {
                    result.Metrics = MetricsCalculator.Compute(copy.Roots, MeasurementContext.Capture("NDMF ビルド後の構成から計算"));
                    bool incomplete = NdmfBuildEvaluator.HasNonNdmfBuildTools(new[] { root });
                    result.Ranks = OptimizerPipeline.EvaluateRanks(copy.Roots, incomplete);
                    foreach (var r in result.Ranks)
                        r.reasons.Insert(0, incomplete
                            ? "NDMF 適用後の構成で評価しましたが、NDMF 以外のビルド時ツール（VRCFury 等）の処理は含まれません。"
                            : "NDMF 適用後（アップロード時と同じ）の構成で評価しました（" + reason + "）。");
                    if (measureSize)
                    {
                        AssetPathUtility.EnsureFolderTracked(temp, created);
                        var prefab = temp + "/" + AssetPathUtility.SanitizeFileName(root.name) + ".prefab";
                        PrefabUtility.SaveAsPrefabAsset(copy.Roots[0], prefab);
                        result.Size = MeasureAsset(prefab, label);
                    }
                }
            }
            catch (Exception e)
            {
                result.Error = e.Message;
            }
            finally
            {
                if (temp != null && AssetDatabase.IsValidFolder(temp)) AssetDatabase.DeleteAsset(temp);
                AssetPathUtility.DeleteEmptyFolders(created);
            }
            return result;
        }

        const string MeasureFolder = UnityAssetsOptimizerInfo.GeneratedRoot + "/_Measure";
        const string BundleName = "uao_measure";

        /// <summary>
        /// AssetBundle をビルドしてサイズを測る（現在のビルドターゲット）。Prefab Asset 以外は一時 Prefab を作り、計測後に削除する。
        /// NDMF の処理は含まない（NDMF がないプロジェクト向け。NDMF がある場合は Evaluate の measureSize を使う）。
        /// </summary>
        public static BundleSize MeasureBundle(GameObject root, string label)
        {
            var result = new BundleSize { Label = label };
            if (root == null)
            {
                result.Error = "対象がありません。";
                return result;
            }
            var created = new List<string>();
            string tempPrefab = null;
            try
            {
                string assetPath;
                if (EditorUtility.IsPersistent(root) && AssetDatabase.IsMainAsset(root))
                {
                    assetPath = AssetDatabase.GetAssetPath(root);
                }
                else
                {
                    AssetPathUtility.EnsureFolderTracked(MeasureFolder, created);
                    tempPrefab = AssetDatabase.GenerateUniqueAssetPath(MeasureFolder + "/" + AssetPathUtility.SanitizeFileName(root.name) + ".prefab");
                    using (var copy = WorkingCopy.Create(new[] { root }))
                        PrefabUtility.SaveAsPrefabAsset(copy.Roots[0], tempPrefab);
                    assetPath = tempPrefab;
                }

                var measured = MeasureAsset(assetPath, label);
                result.Compressed = measured.Compressed;
                result.Uncompressed = measured.Uncompressed;
                result.Error = measured.Error;
            }
            catch (Exception e)
            {
                result.Error = e.Message;
            }
            finally
            {
                if (tempPrefab != null) AssetDatabase.DeleteAsset(tempPrefab);
                AssetPathUtility.DeleteEmptyFolders(created);
            }
            return result;
        }

        static BundleSize MeasureAsset(string assetPath, string label)
        {
            var result = new BundleSize { Label = label };
            var output = Path.Combine(UnityAssetsOptimizerInfo.ProjectRoot, "Library", "UnityAssetsOptimizer", "BundleSize");
            try
            {
                var build = new[] { new AssetBundleBuild { assetBundleName = BundleName, assetNames = new[] { assetPath } } };
                result.Compressed = Build(output, build, BuildAssetBundleOptions.ForceRebuildAssetBundle);
                result.Uncompressed = Build(output, build, BuildAssetBundleOptions.ForceRebuildAssetBundle | BuildAssetBundleOptions.UncompressedAssetBundle);
            }
            catch (Exception e)
            {
                result.Error = e.Message;
            }
            finally
            {
                try { if (Directory.Exists(output)) Directory.Delete(output, true); }
                catch (IOException) { }
            }
            return result;
        }

        static long Build(string output, AssetBundleBuild[] build, BuildAssetBundleOptions options)
        {
            if (Directory.Exists(output)) Directory.Delete(output, true);
            Directory.CreateDirectory(output);
            var manifest = BuildPipeline.BuildAssetBundles(output, build, options, EditorUserBuildSettings.activeBuildTarget);
            var file = Path.Combine(output, BundleName);
            if (manifest == null || !File.Exists(file)) throw new InvalidOperationException("AssetBundle のビルドに失敗しました（コンソールを確認してください）。");
            return new FileInfo(file).Length;
        }

        public static string FormatBytes(long bytes) =>
            bytes >= 1024 * 1024 ? (bytes / (1024.0 * 1024.0)).ToString("0.00") + " MB" : (bytes / 1024.0).ToString("0.0") + " KB";
    }
}
