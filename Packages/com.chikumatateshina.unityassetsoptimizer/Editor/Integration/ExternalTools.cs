using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace ChikumaTateshina.UnityAssetsOptimizer
{
    /// <summary>
    /// 連携する外部ツール。広く使われ、ソースで仕様を確認したものだけを登録する。
    /// 検証済みのバージョン範囲外では使わない（fail-closed）。コンパイル時の依存は持たず、型名で解決する。
    /// </summary>
    public sealed class ExternalTool
    {
        public string Id;
        public string DisplayName;
        public string PackageName;
        public string RepositoryUrl;
        public string VerifiedRange;
        public string VerifiedWith;
        public string MainType;
        /// <summary>VPM ではなく Unity のパッケージレジストリ（Package Manager）から入れるパッケージ。値は追加するときの識別子。</summary>
        public string RegistryPackageId;

        public string InstalledVersion => SdkInfo.Version(PackageName);

        public Type ResolveType(string fullName) =>
            AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(fullName, false)).FirstOrDefault(t => t != null);

        /// <summary>使えるか。使えない場合は理由（未導入・検証範囲外）。</summary>
        public bool IsAvailable(out string reason)
        {
            var version = InstalledVersion;
            if (version == null)
            {
                reason = DisplayName + " が未導入です。" + InstallHint;
                return false;
            }
            if (!CapabilityDatabase.InRange(version, VerifiedRange))
            {
                reason = DisplayName + " " + version + " は動作確認の範囲（" + VerifiedRange + "）外のため使用しません。";
                return false;
            }
            if (MainType != null && ResolveType(MainType) == null)
            {
                reason = DisplayName + " の型 " + MainType + " が見つかりません。";
                return false;
            }
            reason = DisplayName + " " + version + "（" + VerifiedWith + " で動作確認）";
            return true;
        }

        public string InstallHint => RegistryPackageId != null
            ? "Window → Package Manager の Unity Registry から「" + DisplayName + "」を追加してください（ウィンドウの「追加」ボタンでも追加できます）。"
            : "VCC の Settings → Packages → Add Repository に " + RepositoryUrl + " を追加し、プロジェクトに「" + DisplayName + "」を追加してください。";

        public string VccAddRepositoryUrl => "vcc://vpm/addRepo?url=" + Uri.EscapeDataString(RepositoryUrl);
    }

    public static class ExternalTools
    {
        public static readonly ExternalTool Ndmf = new ExternalTool
        {
            Id = "ndmf",
            DisplayName = "NDMF",
            PackageName = "nadena.dev.ndmf",
            RepositoryUrl = "https://vpm.nadena.dev/vpm.json",
            VerifiedRange = ">=1.5.0 <2.0.0",
            VerifiedWith = "1.9.4",
            MainType = "nadena.dev.ndmf.AvatarProcessor",
        };

        /// <summary>Avatar Optimizer（anatawa12）。TraceAndOptimize は「型が唯一の公開 API」とされているため、既定値のまま付ける。</summary>
        public static readonly ExternalTool AvatarOptimizer = new ExternalTool
        {
            Id = "aao",
            DisplayName = "Avatar Optimizer (AAO)",
            PackageName = "com.anatawa12.avatar-optimizer",
            RepositoryUrl = "https://vpm.anatawa12.com/vpm.json",
            VerifiedRange = ">=1.8.0 <2.0.0",
            VerifiedWith = "1.8.14",
            MainType = "Anatawa12.AvatarOptimizer.TraceAndOptimize",
        };

        /// <summary>lilNDMFMeshSimplifier（lilxyzw）。Renderer に付けた quality（0〜1）でビルド時に簡略化する。</summary>
        public static readonly ExternalTool LilMeshSimplifier = new ExternalTool
        {
            Id = "lil-ndmf-mesh-simplifier",
            DisplayName = "lilNDMFMeshSimplifier",
            PackageName = "jp.lilxyzw.ndmfmeshsimplifier",
            RepositoryUrl = "https://lilxyzw.github.io/vpm-repos/vpm.json",
            VerifiedRange = ">=0.3.0 <0.4.0",
            VerifiedWith = "0.3.0",
            MainType = "jp.lilxyzw.ndmfmeshsimplifier.runtime.NDMFMeshSimplifier",
        };

        /// <summary>
        /// Unity 公式の FBX Exporter。ModelExporter.ExportObject(string, Object) で書き出す（4.2.1 のソースで UV 4 組・BlendShape・スキンの書き出しを確認）。
        /// </summary>
        public static readonly ExternalTool FbxExporter = new ExternalTool
        {
            Id = "fbx-exporter",
            DisplayName = "FBX Exporter",
            PackageName = "com.unity.formats.fbx",
            RegistryPackageId = "com.unity.formats.fbx@4.2.1",
            VerifiedRange = ">=4.2.0 <6.0.0",
            VerifiedWith = "4.2.1",
            MainType = "UnityEditor.Formats.Fbx.Exporter.ModelExporter",
        };

        public static IReadOnlyList<ExternalTool> All => new[] { Ndmf, AvatarOptimizer, LilMeshSimplifier };

        /// <summary>NDMF ベース（ビルド後評価で処理結果を再現できる）のビルド時ツールの名前空間。</summary>
        public static readonly string[] NdmfBasedNamespaces =
        {
            "nadena.dev.modular_avatar", "nadena.dev.ndmf", "Anatawa12.AvatarOptimizer", "jp.lilxyzw.ndmfmeshsimplifier",
            "ChikumaTateshina.UnityAssetsOptimizer",
        };
    }

    /// <summary>
    /// NDMF のビルド処理を複製に対して実行し、アップロード時と同じ構成を作る。
    /// 生成物（メッシュ等）は呼び出し側が用意した一時フォルダに保存される（ツールによっては保存先が必須のため）。
    /// </summary>
    public static class NdmfBuildEvaluator
    {
        public static bool IsAvailable(out string reason) => ExternalTools.Ndmf.IsAvailable(out reason);

        /// <summary>NDMF 以外のビルド時変換（VRCFury 等）が含まれるか。含まれるとビルド後評価でも再現できない。</summary>
        public static bool HasNonNdmfBuildTools(IEnumerable<GameObject> roots) =>
            roots.SelectMany(r => r.GetComponentsInChildren<MonoBehaviour>(true)).Where(m => m != null)
                .Select(m => m.GetType().FullName ?? string.Empty)
                .Any(n => n.StartsWith("VF.", StringComparison.Ordinal));

        /// <summary>root（シーン/プレビュー/Prefab Asset のいずれでもよい）の複製を NDMF で処理し、処理後の複製を返す。</summary>
        public static WorkingCopy ProcessCopy(GameObject root, string temporaryAssetFolder)
        {
            var processor = ExternalTools.Ndmf.ResolveType("nadena.dev.ndmf.AvatarProcessor");
            var scopeType = ExternalTools.Ndmf.ResolveType("nadena.dev.ndmf.OverrideTemporaryDirectoryScope");
            var process = processor?.GetMethod("ProcessAvatar", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(GameObject) }, null);
            if (process == null || scopeType == null) throw new MissingMethodException("NDMF の AvatarProcessor.ProcessAvatar が見つかりません。");

            var copy = WorkingCopy.Create(new[] { root });
            try
            {
                using ((IDisposable)Activator.CreateInstance(scopeType, new object[] { temporaryAssetFolder }))
                    process.Invoke(null, new object[] { copy.Roots[0] });
                return copy;
            }
            catch (TargetInvocationException e)
            {
                copy.Dispose();
                throw new InvalidOperationException("NDMF のビルド処理に失敗しました: " + (e.InnerException ?? e).Message, e.InnerException ?? e);
            }
            catch
            {
                copy.Dispose();
                throw;
            }
        }
    }
}
