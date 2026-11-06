using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace ChikumaTateshina.AssetsOptimizer
{
    /// <summary>
    /// テクスチャの GPU メモリ推定。形式・ミップ・解像度からの計算値であり、実際の常駐 VRAM ではない。
    /// </summary>
    public static class TextureMemoryEstimator
    {
        public const string MethodImported = "インポート済み graphicsFormat × 解像度 × ミップ（現在の Build Target）";
        public const string MethodPlanned = "計画した Importer 設定から形式を推定 × 解像度 × ミップ";

        public static long Estimate(Texture2D tex)
        {
            return Estimate(tex.graphicsFormat, tex.width, tex.height, tex.mipmapCount);
        }

        public static long Estimate(GraphicsFormat format, int width, int height, int mipCount)
        {
            uint bw = Math.Max(1u, GraphicsFormatUtility.GetBlockWidth(format));
            uint bh = Math.Max(1u, GraphicsFormatUtility.GetBlockHeight(format));
            uint bs = GraphicsFormatUtility.GetBlockSize(format);
            long total = 0;
            int w = width, h = height;
            for (int i = 0; i < Math.Max(1, mipCount); i++)
            {
                total += (long)((w + bw - 1) / bw) * ((h + bh - 1) / bh) * bs;
                w = Math.Max(1, w / 2);
                h = Math.Max(1, h / 2);
            }
            return total;
        }

        /// <summary>
        /// Dry Run の生成テクスチャ（未インポート）の推定。現在の Build Target で形式を一意に決められない場合は null。
        /// </summary>
        public static long? EstimatePlanned(GeneratedTexture t, out string method)
        {
            method = MethodPlanned;
            var plan = t.Import;
            int w = t.Width, h = t.Height;
            int max = Math.Max(w, h);
            if (max > plan.MaxSize)
            {
                float s = (float)plan.MaxSize / max;
                w = Math.Max(1, Mathf.RoundToInt(w * s));
                h = Math.Max(1, Mathf.RoundToInt(h * s));
            }
            int mips = 1 + (int)Math.Floor(Math.Log(Math.Max(w, h), 2));

            GraphicsFormat format;
            var target = EditorUserBuildSettings.activeBuildTarget;
            bool mobile = target == BuildTarget.Android || target == BuildTarget.iOS;
            if (plan.Compression == TextureImporterCompression.Uncompressed)
            {
                format = GraphicsFormat.R8G8B8A8_UNorm;
            }
            else if (mobile)
            {
                if (plan.MobileAstcBlock <= 0)
                {
                    method = "モバイル既定形式は Player 設定に依存するため推定不可";
                    return null;
                }
                format = AstcFormat(plan.MobileAstcBlock);
                method += "（ASTC " + plan.MobileAstcBlock + "x" + plan.MobileAstcBlock + "）";
            }
            else if (target == BuildTarget.StandaloneWindows || target == BuildTarget.StandaloneWindows64 ||
                     target == BuildTarget.StandaloneOSX || target == BuildTarget.StandaloneLinux64)
            {
                format = plan.NormalMap || (t.HasAlpha && plan.AlphaFromInput) ? GraphicsFormat.RGBA_DXT5_UNorm : GraphicsFormat.RGBA_DXT1_UNorm;
                method += plan.NormalMap || (t.HasAlpha && plan.AlphaFromInput) ? "（BC3/BC5 相当 1B/px）" : "（BC1 0.5B/px）";
            }
            else
            {
                method = "Build Target " + target + " の既定形式は推定対象外";
                return null;
            }
            if (plan.Crunch) method += "（Crunch はディスク容量のみ影響）";
            return Estimate(format, w, h, mips);
        }

        static GraphicsFormat AstcFormat(int block)
        {
            switch (block)
            {
                case 4: return GraphicsFormat.RGBA_ASTC4X4_UNorm;
                case 5: return GraphicsFormat.RGBA_ASTC5X5_UNorm;
                case 8: return GraphicsFormat.RGBA_ASTC8X8_UNorm;
                default: return GraphicsFormat.RGBA_ASTC6X6_UNorm;
            }
        }
    }
}
