using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace ChikumaTateshina.AssetsOptimizer
{
    /// <summary>計測条件。前後比較で不一致があれば警告する。</summary>
    [Serializable]
    public class MeasurementContext
    {
        public string contextId;
        public string unityVersion;
        public string sdkVersions;
        public string buildTarget;
        public string qualityLevel;
        public string renderPipeline;
        public string colorSpace;
        public string measurement;
        public string notes;

        public static MeasurementContext Capture(string measurement)
        {
            var ctx = new MeasurementContext
            {
                unityVersion = Application.unityVersion,
                sdkVersions = SdkInfo.Describe(),
                buildTarget = EditorUserBuildSettings.activeBuildTarget.ToString(),
                qualityLevel = QualitySettings.names.Length > 0 ? QualitySettings.names[QualitySettings.GetQualityLevel()] : "(none)",
                renderPipeline = GraphicsSettings.currentRenderPipeline == null ? "Built-in" : GraphicsSettings.currentRenderPipeline.GetType().Name,
                colorSpace = PlayerSettings.colorSpace.ToString(),
                measurement = measurement,
                notes = "構造値は階層・アセットから計算。描画系（Draw Calls 等）は計測していません。",
            };
            ctx.contextId = HashUtility.Short(HashUtility.Sha256(string.Join("|",
                ctx.unityVersion, ctx.sdkVersions, ctx.buildTarget, ctx.qualityLevel, ctx.renderPipeline, ctx.colorSpace)), 12);
            return ctx;
        }

        public List<string> CompareWith(MeasurementContext other)
        {
            var diffs = new List<string>();
            if (other == null) return diffs;
            void Check(string label, string a, string b)
            {
                if (a != b) diffs.Add(label + ": " + a + " ≠ " + b);
            }
            Check("Unity", unityVersion, other.unityVersion);
            Check("SDK", sdkVersions, other.sdkVersions);
            Check("BuildTarget", buildTarget, other.buildTarget);
            Check("Quality", qualityLevel, other.qualityLevel);
            Check("RenderPipeline", renderPipeline, other.renderPipeline);
            Check("ColorSpace", colorSpace, other.colorSpace);
            return diffs;
        }
    }

    /// <summary>VRChat SDK のバージョン取得。コンパイル時参照は持たず、パッケージ情報から読む。</summary>
    public static class SdkInfo
    {
        public static readonly string[] SdkPackages = { "com.vrchat.base", "com.vrchat.avatars", "com.vrchat.worlds" };

        public static string Version(string packageName)
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/" + packageName);
            return info?.version;
        }

        public static string Describe()
        {
            var parts = new List<string>();
            foreach (var p in SdkPackages)
            {
                var v = Version(p);
                if (v != null) parts.Add(p + "@" + v);
            }
            return parts.Count == 0 ? "VRChat SDK 未検出" : string.Join(", ", parts);
        }
    }
}
