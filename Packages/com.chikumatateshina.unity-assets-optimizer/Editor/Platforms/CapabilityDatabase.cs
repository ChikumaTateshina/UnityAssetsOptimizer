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
        public string verification;
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

        /// <summary>
        /// 変換を有効化できるか。検証済みエントリがあり、かつプロジェクトの SDK バージョンが検証範囲内の場合のみ true（fail-closed）。
        /// </summary>
        public static bool IsConversionEnabled(VrcPlatform platform, ContentPreset content, out string reason)
        {
            if (platform == VrcPlatform.PC)
            {
                reason = "PC 版は元データを維持します（変換対象外）。";
                return false;
            }
            var entries = LoadAll().Where(c => c.platform == platform.ToString() && c.contentType == content.ToString() && c.verified).ToList();
            if (entries.Count == 0)
            {
                reason = platform + "/" + content + " の検証済み能力 DB がありません。";
                return false;
            }
            var sdkPackage = content == ContentPreset.Avatar ? "com.vrchat.avatars" : "com.vrchat.worlds";
            var sdk = SdkInfo.Version(sdkPackage);
            if (sdk == null)
            {
                reason = sdkPackage + " が見つかりません（VRChat SDK 未導入）。";
                return false;
            }
            var entry = entries.FirstOrDefault(e => InRange(sdk, e.vrcSdkVersionRange) && InRange(Application.unityVersion, e.unityVersionRange));
            if (entry == null)
            {
                reason = "SDK " + sdk + " / Unity " + Application.unityVersion + " は検証範囲（" + string.Join(", ", entries.Select(e => e.vrcSdkVersionRange)) + "）外のため無効です。";
                return false;
            }
            reason = "検証済み: " + entry.verification;
            return true;
        }

        /// <summary>">=a <b" 形式の範囲判定。</summary>
        public static bool InRange(string version, string range)
        {
            if (string.IsNullOrEmpty(range)) return false;
            if (!TryParse(version, out var v)) return false;
            foreach (var token in range.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.StartsWith(">=", StringComparison.Ordinal)) { if (!TryParse(token.Substring(2), out var lo) || v < lo) return false; }
                else if (token.StartsWith("<", StringComparison.Ordinal)) { if (!TryParse(token.Substring(1), out var hi) || v >= hi) return false; }
                else return false;
            }
            return true;
        }

        static bool TryParse(string s, out Version v)
        {
            v = null;
            if (string.IsNullOrEmpty(s)) return false;
            var digits = new string(s.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()).Trim('.');
            return Version.TryParse(digits.Count(c => c == '.') == 0 ? digits + ".0" : digits, out v);
        }
    }
}
