using System.IO;
using UnityEngine;

namespace ChikumaTateshina.AssetsOptimizer
{
    public static class VaoInfo
    {
        public const string PackageName = "com.chikumatateshina.unity-assets-optimizer";
        public const string ToolVersion = "0.1.0";
        public const int ManifestSchemaVersion = 1;
        public const string MenuRoot = "Tools/VRChat Asset Optimizer/";

        public const string PackageRoot = "Packages/" + PackageName;

        /// <summary>生成物の管理領域。原本とは別領域に生成する。</summary>
        public const string GeneratedRoot = "Assets/VAOGenerated";

        public const string SnapshotFolderName = "Snapshot~";   // "~" 付きフォルダは Unity にインポートされない
        public const string StagingFolderName = "_Staging";
        public const string GeneratedFolderName = "Generated";
        public const string ReportsFolderName = "Reports";
        public const string ManifestFileName = "Manifest.json";

        public static string ProjectRoot => Path.GetDirectoryName(Application.dataPath);

        public static string RunFolder(string runId) => GeneratedRoot + "/" + runId;

        public static string SnapshotFolderFull(string runId) =>
            Path.Combine(ProjectRoot, RunFolder(runId), SnapshotFolderName);

        /// <summary>失敗・キャンセル時のログ置き場。Assets 外なのでインポートされない。</summary>
        public static string FailureLogFolderFull => Path.Combine(ProjectRoot, "Library", "VAOLogs");
    }
}
