using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ChikumaTateshina.UnityAssetsOptimizer
{
    public sealed class AtlasPackResult
    {
        public int Width;
        public int Height;
        /// <summary>最大サイズに収めるために全要素へ一律に掛けた縮小率（1 = 等倍）。</summary>
        public float Scale;
        /// <summary>入力順に対応する配置矩形（パディングを含まない内側の矩形）。</summary>
        public RectInt[] Rects;
    }

    /// <summary>
    /// 決定的なシェルフパッカー。同じ入力からは常に同じレイアウトを返す（Texture2D.PackTextures は使わない）。
    /// </summary>
    public static class AtlasPacker
    {
        public static AtlasPackResult Pack(IList<Vector2Int> sizes, int padding, int maxSize)
        {
            if (sizes == null || sizes.Count == 0) throw new ArgumentException("パック対象がありません。");
            if (maxSize < 1) throw new ArgumentException("maxSize が不正です。");
            padding = Math.Max(0, padding);

            for (float scale = 1f; scale >= 1f / 64f; scale *= 0.5f)
            {
                var scaled = sizes.Select(s => new Vector2Int(
                    Math.Max(1, (int)Math.Ceiling(s.x * scale)),
                    Math.Max(1, (int)Math.Ceiling(s.y * scale)))).ToList();

                AtlasPackResult best = null;
                for (int width = 1; width <= maxSize; width *= 2)
                {
                    var rects = ShelfPack(scaled, padding, width, out int usedHeight);
                    if (rects == null) continue;
                    int height = NextPowerOfTwo(usedHeight);
                    if (height > maxSize) continue;
                    if (best == null || Better(width, height, best.Width, best.Height))
                        best = new AtlasPackResult { Width = width, Height = height, Scale = scale, Rects = rects };
                }
                if (best != null) return best;
            }
            throw new InvalidOperationException("アトラスに収まりません（最大サイズ " + maxSize + "）。");
        }

        static bool Better(int w, int h, int bw, int bh)
        {
            long area = (long)w * h, bestArea = (long)bw * bh;
            if (area != bestArea) return area < bestArea;
            int maxDim = Math.Max(w, h), bestMaxDim = Math.Max(bw, bh);
            if (maxDim != bestMaxDim) return maxDim < bestMaxDim;
            return w < bw;
        }

        static RectInt[] ShelfPack(List<Vector2Int> sizes, int padding, int width, out int usedHeight)
        {
            usedHeight = 0;
            var order = Enumerable.Range(0, sizes.Count)
                .OrderByDescending(i => sizes[i].y)
                .ThenByDescending(i => sizes[i].x)
                .ThenBy(i => i)
                .ToList();
            var rects = new RectInt[sizes.Count];
            int x = 0, y = 0, shelfHeight = 0;
            foreach (var i in order)
            {
                int pw = sizes[i].x + padding * 2;
                int ph = sizes[i].y + padding * 2;
                if (pw > width) return null;
                if (x + pw > width)
                {
                    y += shelfHeight;
                    x = 0;
                    shelfHeight = 0;
                }
                rects[i] = new RectInt(x + padding, y + padding, sizes[i].x, sizes[i].y);
                x += pw;
                shelfHeight = Math.Max(shelfHeight, ph);
            }
            usedHeight = y + shelfHeight;
            return rects;
        }

        public static int NextPowerOfTwo(int v)
        {
            int p = 1;
            while (p < v) p *= 2;
            return p;
        }
    }

    public static class AtlasComposer
    {
        public struct Source
        {
            public Color32[] Pixels;
            public int Width;
            public int Height;
            public RectInt Destination;
        }

        /// <summary>
        /// 各ソースを配置矩形へバイリニアで再サンプルし、パディング領域には端の画素を延長する（mip bleed 対策）。
        /// </summary>
        public static Color32[] Compose(int width, int height, IList<Source> sources, int padding, Color32 fill)
        {
            var dest = new Color32[width * height];
            for (int i = 0; i < dest.Length; i++) dest[i] = fill;

            foreach (var src in sources)
            {
                var r = src.Destination;
                int x0 = Math.Max(0, r.x - padding), x1 = Math.Min(width, r.xMax + padding);
                int y0 = Math.Max(0, r.y - padding), y1 = Math.Min(height, r.yMax + padding);
                for (int y = y0; y < y1; y++)
                {
                    float v = Mathf.Clamp01((y - r.y + 0.5f) / r.height);
                    for (int x = x0; x < x1; x++)
                    {
                        float u = Mathf.Clamp01((x - r.x + 0.5f) / r.width);
                        dest[y * width + x] = SampleBilinear(src.Pixels, src.Width, src.Height, u, v);
                    }
                }
            }
            return dest;
        }

        static Color32 SampleBilinear(Color32[] px, int w, int h, float u, float v)
        {
            float fx = u * w - 0.5f, fy = v * h - 0.5f;
            int ix = Mathf.FloorToInt(fx), iy = Mathf.FloorToInt(fy);
            float tx = fx - ix, ty = fy - iy;
            int x0 = Mathf.Clamp(ix, 0, w - 1), x1 = Mathf.Clamp(ix + 1, 0, w - 1);
            int y0 = Mathf.Clamp(iy, 0, h - 1), y1 = Mathf.Clamp(iy + 1, 0, h - 1);
            var a = Color32.LerpUnclamped(px[y0 * w + x0], px[y0 * w + x1], tx);
            var b = Color32.LerpUnclamped(px[y1 * w + x0], px[y1 * w + x1], tx);
            return Color32.LerpUnclamped(a, b, ty);
        }
    }
}
