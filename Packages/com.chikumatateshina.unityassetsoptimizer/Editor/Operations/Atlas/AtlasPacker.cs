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
        /// <summary>全要素へ一律に掛けた倍率（1 = 等倍、1 未満は最大サイズに収めるための縮小、1 超は余白を埋めるための拡大）。</summary>
        public float Scale;
        /// <summary>入力順に対応する配置矩形（パディングを含まない内側の矩形）。余白を埋めるため縦横の倍率が異なる場合がある。</summary>
        public RectInt[] Rects;
        /// <summary>キャンバスのうち領域（パディングを含む）が占める割合。</summary>
        public float FillRatio;
    }

    /// <summary>
    /// 決定的なアトラスパッカー（MaxRects・Best Short Side Fit）。同じ入力からは常に同じレイアウトを返す。
    /// 元の解像度を落とさずに収まる最小の 2 のべき乗の正方形を選び（最大サイズに収まらなければ一律に縮小）、
    /// 一律の倍率を二分探索で最大化したうえで、残った余白へ各領域を広げてキャンバス全体を使う。
    /// </summary>
    public static class AtlasPacker
    {
        public static AtlasPackResult Pack(IList<Vector2Int> sizes, int padding, int maxSize) =>
            Pack(sizes.Select(s => (Vector2)s).ToList(), padding, maxSize);

        public static AtlasPackResult Pack(IList<Vector2> sizes, int padding, int maxSize)
        {
            if (sizes == null || sizes.Count == 0) throw new ArgumentException("パック対象がありません。");
            if (maxSize < 1) throw new ArgumentException("maxSize が不正です。");
            padding = Math.Max(0, padding);

            int limit = 1;
            while (limit * 2 <= maxSize) limit *= 2;
            int canvas = limit;
            for (int size = 1; size <= limit; size *= 2)
                if (TryPack(sizes, padding, size, 1f) != null)
                {
                    canvas = size;
                    break;
                }

            // 収まる最大の一律倍率を二分探索（等倍で収まる場合は拡大して余白を減らす）
            double sumArea = sizes.Sum(s => (double)(s.x + padding * 2) * (s.y + padding * 2));
            float lo = 0f, hi = (float)Math.Max(1.0, Math.Sqrt((double)canvas * canvas / Math.Max(1.0, sumArea)) * 1.01);
            RectInt[] best = null;
            float bestScale = 0f;
            for (int iter = 0; iter < 24; iter++)
            {
                float mid = (lo + hi) / 2f;
                var rects = TryPack(sizes, padding, canvas, mid);
                if (rects != null)
                {
                    best = rects;
                    bestScale = mid;
                    lo = mid;
                }
                else hi = mid;
            }
            if (best == null || bestScale < 1f / 64f)
                throw new InvalidOperationException("アトラスに収まりません（最大サイズ " + maxSize + "）。");

            Expand(best, padding, canvas);
            long used = best.Sum(r => (long)(r.width + padding * 2) * (r.height + padding * 2));
            return new AtlasPackResult
            {
                Width = canvas, Height = canvas, Scale = bestScale, Rects = best,
                FillRatio = (float)Math.Min(1.0, used / ((double)canvas * canvas)),
            };
        }

        static RectInt[] TryPack(IList<Vector2> sizes, int padding, int canvas, float scale)
        {
            var scaled = sizes.Select(s => new Vector2Int(
                Math.Max(1, (int)Math.Floor(s.x * scale)),
                Math.Max(1, (int)Math.Floor(s.y * scale)))).ToList();
            var order = Enumerable.Range(0, scaled.Count)
                .OrderByDescending(i => (long)scaled[i].x * scaled[i].y)
                .ThenByDescending(i => Math.Max(scaled[i].x, scaled[i].y))
                .ThenBy(i => i)
                .ToList();
            var free = new List<RectInt> { new RectInt(0, 0, canvas, canvas) };
            var rects = new RectInt[scaled.Count];
            foreach (var i in order)
            {
                int w = scaled[i].x + padding * 2, h = scaled[i].y + padding * 2;
                int bestIndex = -1, bestShort = int.MaxValue, bestLong = int.MaxValue;
                for (int f = 0; f < free.Count; f++)
                {
                    var fr = free[f];
                    if (fr.width < w || fr.height < h) continue;
                    int dx = fr.width - w, dy = fr.height - h;
                    int shortSide = Math.Min(dx, dy), longSide = Math.Max(dx, dy);
                    if (shortSide < bestShort || (shortSide == bestShort && longSide < bestLong))
                    {
                        bestIndex = f;
                        bestShort = shortSide;
                        bestLong = longSide;
                    }
                }
                if (bestIndex < 0) return null;
                var placed = new RectInt(free[bestIndex].x, free[bestIndex].y, w, h);
                rects[i] = new RectInt(placed.x + padding, placed.y + padding, scaled[i].x, scaled[i].y);
                SplitFree(free, placed);
            }
            return rects;
        }

        static void SplitFree(List<RectInt> free, RectInt used)
        {
            var result = new List<RectInt>();
            foreach (var f in free)
            {
                if (!f.Overlaps(used))
                {
                    result.Add(f);
                    continue;
                }
                if (used.x > f.x) result.Add(new RectInt(f.x, f.y, used.x - f.x, f.height));
                if (used.xMax < f.xMax) result.Add(new RectInt(used.xMax, f.y, f.xMax - used.xMax, f.height));
                if (used.y > f.y) result.Add(new RectInt(f.x, f.y, f.width, used.y - f.y));
                if (used.yMax < f.yMax) result.Add(new RectInt(f.x, used.yMax, f.width, f.yMax - used.yMax));
            }
            // 他の空き矩形に含まれるものを除く（同一のものは先の 1 つを残す）
            free.Clear();
            for (int i = 0; i < result.Count; i++)
            {
                bool contained = false;
                for (int j = 0; j < result.Count && !contained; j++)
                    if (i != j && Contains(result[j], result[i]) && (!Contains(result[i], result[j]) || j < i)) contained = true;
                if (!contained) free.Add(result[i]);
            }
        }

        static bool Contains(RectInt a, RectInt b) => b.x >= a.x && b.y >= a.y && b.xMax <= a.xMax && b.yMax <= a.yMax;

        /// <summary>各領域を右・上の空きへ広げる（他の領域とパディングを含めて重ならない範囲）。縮小はしない。</summary>
        static void Expand(RectInt[] rects, int padding, int canvas)
        {
            var order = Enumerable.Range(0, rects.Length).OrderBy(i => rects[i].x + rects[i].y).ThenBy(i => i).ToList();
            for (int pass = 0; pass < 2; pass++)
                foreach (var i in order)
                {
                    var r = rects[i];
                    int limitX = canvas - padding;
                    for (int j = 0; j < rects.Length; j++)
                        if (j != i && rects[j].yMax + padding > r.y - padding && rects[j].y - padding < r.yMax + padding && rects[j].x >= r.xMax)
                            limitX = Math.Min(limitX, rects[j].x - padding * 2);
                    r.width = Math.Max(r.width, limitX - r.x);
                    int limitY = canvas - padding;
                    for (int j = 0; j < rects.Length; j++)
                        if (j != i && rects[j].xMax + padding > r.x - padding && rects[j].x - padding < r.xMax + padding && rects[j].y >= r.yMax)
                            limitY = Math.Min(limitY, rects[j].y - padding * 2);
                    r.height = Math.Max(r.height, limitY - r.y);
                    rects[i] = r;
                }
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
