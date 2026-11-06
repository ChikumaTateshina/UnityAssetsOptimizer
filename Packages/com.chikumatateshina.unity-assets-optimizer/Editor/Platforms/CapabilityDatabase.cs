using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace ChikumaTateshina.AssetsOptimizer
{
    /// <summary>
    /// プラットフォーム能力 DB（Phase 5）。スキーマのみ定義し、verified=false のエントリは実データとして採用しない。
    /// Android と iOS は独立したエントリとして管理する。
    /// </summary>
    [Serializable]
    public class PlatformCapability
    {
        public int schemaVersion;
        public string unityVersionRange;
        public string vrcSdkVersionRange;
        public string platform;
        public string contentType;
        public string[] shaderAdapterIds;
        public string[] textureFormats;
        public string performanceRuleSetId;
        public bool verified;
        [NonSerialized] public string filePath;
    }

    public static class CapabilityDatabase
    {
        public static List<PlatformCapability> LoadAll()
        {
            var list = new List<PlatformCapability>();
            var dir = AssetPathUtility.ToFullPath(VaoInfo.PackageRoot + "/Editor/Platforms/Data/Capabilities");
            if (dir == null || !Directory.Exists(dir)) return list;
            foreach (var file in Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
            {
                try
                {
                    var cap = JsonUtility.FromJson<PlatformCapability>(File.ReadAllText(file));
                    if (cap == null) continue;
                    cap.filePath = file;
                    list.Add(cap);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[VAO] 能力 DB を読み込めません: " + file + " (" + e.Message + ")");
                }
            }
            return list;
        }

        /// <summary>変換を有効化できるか。検証済みエントリがなければ常に false（変換不可）。</summary>
        public static bool IsConversionEnabled(VrcPlatform platform, ContentPreset content, out string reason)
        {
            var entries = LoadAll().Where(c => c.platform == platform.ToString() && c.contentType == content.ToString()).ToList();
            if (platform == VrcPlatform.PC)
            {
                reason = "PC 版は元データを維持します（変換対象外）。";
                return false;
            }
            if (!entries.Any(e => e.verified))
            {
                reason = platform + "/" + content + " の検証済み能力 DB がありません。変換は Phase 5 で SDK・公式仕様の検証後に有効化します。";
                return false;
            }
            reason = "変換処理は未実装です（Phase 5）。";
            return false;
        }
    }
}
