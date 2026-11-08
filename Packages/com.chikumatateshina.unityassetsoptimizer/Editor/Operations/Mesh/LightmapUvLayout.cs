using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace ChikumaTateshina.UnityAssetsOptimizer
{
    /// <summary>
    /// ライトマップ UV（UV1）の点検・島の並べ直し・展開。
    /// 既存の実装を参考にしている:
    /// - Unity の Generate Lightmap UVs（Margin Method = Calculate）: 余白を想定ライトマップ解像度のテクセル数で決める。
    /// - xatlas（Bakery・Godot が使う MIT のライブラリ）: 島（チャート）単位で、3D の大きさに比例した一律の倍率（縦横比を保つ）で、
    ///   回転も使って重ならないように詰める。
    /// 既に展開されている UV1 は島の形を変えない（重なっていれば島を並べ直すだけ）。UV1 がない・島そのものが折り重なっている場合だけ
    /// Unity の自動展開を使う。
    /// </summary>
    public static class LightmapUvLayout
    {
        /// <summary>島どうしの間隔（想定ライトマップ解像度のテクセル数。Unity・Bakery の推奨は 2 以上）。</summary>
        public const int PaddingTexels = 2;
        /// <summary>島の中で三角形が折り重なっている割合の上限（これを超える島があれば展開し直す）。</summary>
        const float MaxSelfOverlap = 0.02f;
        /// <summary>異なる島が同じテクセルを共有している割合の上限（これを超えたら島を並べ直す）。</summary>
        const float MaxChartOverlap = 0.001f;

        public enum Outcome { Kept, Repacked, Unwrapped }

        sealed class Chart
        {
            public int Source;
            public List<int> Triangles = new List<int>(); // 三角形の先頭インデックス（all 配列内）
            public HashSet<int> Vertices = new HashSet<int>();
            public Rect Bounds;
        }

        /// <summary>Transform の拡大率（面積の見積もり用。各軸の平均）。</summary>
        public static float WorldScale(Transform t)
        {
            var s = t.lossyScale;
            return Math.Max(1e-6f, (Math.Abs(s.x) + Math.Abs(s.y) + Math.Abs(s.z)) / 3f);
        }

        /// <summary>シーンのライトマップ解像度（texel/unit）。設定がなければ Unity の既定値 40。</summary>
        public static float LightmapResolution()
        {
            try
            {
                var settings = Lightmapping.lightingSettings;
                if (settings != null && settings.lightmapResolution > 0) return settings.lightmapResolution;
            }
            catch (Exception)
            {
                // LightingSettings が割り当てられていないシーン
            }
            return 40f;
        }

        /// <summary>メッシュ全体が収まる想定ライトマップの一辺のテクセル数（Unity は表面積と解像度で区画を決める）。</summary>
        public static int ExpectedTexels(double worldArea, float resolution) =>
            Mathf.Clamp(Mathf.CeilToInt((float)Math.Sqrt(Math.Max(1e-8, worldArea) / 0.7) * resolution), 32, 2048);

        /// <summary>
        /// ライトマップ用に使えるよう UV1 を整える（メッシュを直接変更する）。
        /// UV1 があり島が折り重なっていなければ島の形は保ち、異なる島が重なっていれば並べ直す。なければ・壊れていれば展開する。
        /// </summary>
        public static Outcome Ensure(Mesh mesh, float worldScale = 1f)
        {
            float resolution = LightmapResolution();
            double area = WorldArea(mesh) * worldScale * worldScale;
            int texels = ExpectedTexels(area, resolution);
            if (NeedsUnwrap(mesh, 0, mesh.vertexCount))
            {
                Unwrap(mesh, texels);
                Pack(mesh, null, worldScale);
                return Outcome.Unwrapped;
            }
            if (ChartOverlap(mesh, Math.Min(texels, 1024)) <= MaxChartOverlap) return Outcome.Kept;
            Pack(mesh, null, worldScale);
            return Outcome.Repacked;
        }

        /// <summary>
        /// 結合前のソースを点検する。展開済み（UV1 があり島が折り重なっていない）ならそのまま返し、
        /// そうでなければ UV1 を展開した一時的な複製を返す（unwrapped = true。呼び出し側で破棄する）。
        /// </summary>
        public static Mesh PrepareSource(Mesh source, float worldScale, out bool unwrapped)
        {
            unwrapped = NeedsUnwrap(source, 0, source.vertexCount);
            if (!unwrapped) return source;
            var copy = Object.Instantiate(source);
            copy.name = source.name;
            double area = WorldArea(copy) * worldScale * worldScale;
            Unwrap(copy, ExpectedTexels(area, LightmapResolution()));
            return copy;
        }

        /// <summary>Unity の自動展開（余白は想定ライトマップ解像度で PaddingTexels テクセル）。</summary>
        static void Unwrap(Mesh mesh, int texels)
        {
            UnwrapParam.SetDefaults(out var param);
            param.packMargin = Mathf.Clamp((float)PaddingTexels / texels, 0.001f, 0.06f);
            Unwrapping.GenerateSecondaryUVSet(mesh, param);
        }

        /// <summary>UV1 がない、面積がない、または島そのものが折り重なっている（そのままでは光が混ざる）。</summary>
        static bool NeedsUnwrap(Mesh mesh, int start, int end)
        {
            if (!mesh.HasVertexAttribute(VertexAttribute.TexCoord1)) return true;
            var uv = new List<Vector2>();
            mesh.GetUVs(1, uv);
            var tris = Triangles(mesh);
            var charts = FindCharts(mesh, uv, tris, new[] { start }, end);
            double uvArea = 0, folded = 0;
            foreach (var c in charts)
            {
                double a = UvArea(uv, tris, c.Triangles);
                uvArea += a;
                folded += SelfOverlap(uv, tris, c) * a;
            }
            if (charts.Count == 0) return false;
            if (uvArea < 1e-10) return true;
            return folded / uvArea > MaxSelfOverlap;
        }

        /// <summary>
        /// UV1 の島を、ソースごとの一律の倍率（3D の面積 / UV の面積。縦横比と島どうしの大きさの比を保つ）で、
        /// 重ならないよう 0..1 に詰め直す。vertexOffsets は結合したソースごとの頂点の開始位置（null なら 1 つ）。
        /// </summary>
        public static void Pack(Mesh mesh, int[] vertexOffsets, float worldScale = 1f)
        {
            if (!mesh.HasVertexAttribute(VertexAttribute.TexCoord1)) Unwrap(mesh, 256);
            var offsets = vertexOffsets ?? new[] { 0 };
            var uv = new List<Vector2>();
            mesh.GetUVs(1, uv);
            var tris = Triangles(mesh);
            var charts = FindCharts(mesh, uv, tris, offsets, mesh.vertexCount);
            if (charts.Count == 0) return;

            // ソースごとの倍率（UV 1 あたりの 3D の長さ）
            var positions = new List<Vector3>();
            mesh.GetVertices(positions);
            var worldArea = new double[offsets.Length];
            var uvArea = new double[offsets.Length];
            foreach (var c in charts)
            {
                foreach (var t in c.Triangles)
                {
                    var a = positions[tris[t]];
                    worldArea[c.Source] += Vector3.Cross(positions[tris[t + 1]] - a, positions[tris[t + 2]] - a).magnitude * 0.5;
                }
                uvArea[c.Source] += UvArea(uv, tris, c.Triangles);
            }
            var scale = new double[offsets.Length];
            for (int s = 0; s < offsets.Length; s++)
                scale[s] = uvArea[s] > 1e-12 ? Math.Sqrt(worldArea[s] / uvArea[s]) : 0;
            double fallback = scale.Where(v => v > 0).DefaultIfEmpty(1).Average();
            for (int s = 0; s < offsets.Length; s++)
                if (scale[s] <= 0) scale[s] = fallback;

            // 想定ライトマップ解像度で PaddingTexels の余白。精度のため 4096 以上の仮想解像度で詰める
            // 島が多く余白だけで埋まる場合（島の面積が 3 割未満）は、想定解像度を倍にして余白を相対的に小さくする（全体を縮めて潰さない）
            int texels = ExpectedTexels(worldArea.Sum() * worldScale * worldScale, LightmapResolution());
            var sizes = charts.Select(c => new Vector2((float)(c.Bounds.width * scale[c.Source]), (float)(c.Bounds.height * scale[c.Source]))).ToList();
            ChartPacker.Result packed = null;
            int canvas = 0;
            for (int attempt = 0; attempt < 6; attempt++, texels *= 2)
            {
                int k = Math.Max(1, Mathf.CeilToInt(4096f / texels));
                canvas = texels * k;
                packed = ChartPacker.Pack(sizes, PaddingTexels * k, canvas);
                if (packed.ContentRatio >= 0.3f) break;
            }

            var result = new List<Vector2>(uv);
            for (int i = 0; i < charts.Count; i++)
            {
                var c = charts[i];
                var r = packed.Rects[i];
                double f = scale[c.Source] * packed.Scale;
                foreach (var v in c.Vertices)
                {
                    double dx = uv[v].x - c.Bounds.x, dy = uv[v].y - c.Bounds.y;
                    // 回転は 90 度（向きは保たれる）
                    double x = packed.Rotated[i] ? r.x + dy * f : r.x + dx * f;
                    double y = packed.Rotated[i] ? r.y + (c.Bounds.width - dx) * f : r.y + dy * f;
                    result[v] = new Vector2((float)(x / canvas), (float)(y / canvas));
                }
            }
            mesh.SetUVs(1, result);
        }

        /// <summary>
        /// 異なる島が同じテクセルを共有している割合（指定の解像度で、三角形を半テクセル広げて塗る。Unity のライトマップの UV Overlap 表示に相当）。
        /// UV1 がなければ 1。
        /// </summary>
        public static float ChartOverlap(Mesh mesh, int resolution)
        {
            if (!mesh.HasVertexAttribute(VertexAttribute.TexCoord1)) return 1f;
            var uv = new List<Vector2>();
            mesh.GetUVs(1, uv);
            var tris = Triangles(mesh);
            var charts = FindCharts(mesh, uv, tris, new[] { 0 }, mesh.vertexCount);
            int n = Math.Max(8, resolution);
            var owner = new int[n * n];
            var conflict = new bool[n * n];
            for (int ci = 0; ci < charts.Count; ci++)
                foreach (var t in charts[ci].Triangles)
                    RasterizeConservative(uv[tris[t]] * n, uv[tris[t + 1]] * n, uv[tris[t + 2]] * n, n, (x, y) =>
                    {
                        int i = y * n + x;
                        if (owner[i] == 0) owner[i] = ci + 1;
                        else if (owner[i] != ci + 1) conflict[i] = true;
                    });
            int covered = 0, multi = 0;
            for (int i = 0; i < owner.Length; i++)
            {
                if (owner[i] != 0) covered++;
                if (conflict[i]) multi++;
            }
            return covered == 0 ? 1f : (float)multi / covered;
        }

        static int[] Triangles(Mesh mesh)
        {
            var all = new List<int>();
            for (int sm = 0; sm < mesh.subMeshCount; sm++)
                if (mesh.GetTopology(sm) == MeshTopology.Triangles) all.AddRange(mesh.GetTriangles(sm));
            return all.ToArray();
        }

        /// <summary>
        /// 島 = UV1 でつながった三角形のまとまり。頂点の共有に加え、同じ位置・同じ UV1 の頂点（法線の違い等で分かれた頂点）もつなぐ。
        /// ソース（頂点範囲）をまたいではつながない。
        /// </summary>
        static List<Chart> FindCharts(Mesh mesh, List<Vector2> uv, int[] tris, int[] offsets, int vertexCount)
        {
            var positions = new List<Vector3>();
            mesh.GetVertices(positions);
            int SourceOf(int v)
            {
                int s = Array.BinarySearch(offsets, v);
                if (s < 0) s = ~s - 1;
                while (s + 1 < offsets.Length && offsets[s + 1] == v) s++;
                return Math.Max(0, s);
            }
            var parent = Enumerable.Range(0, vertexCount).ToArray();
            int Find(int v)
            {
                while (parent[v] != v)
                {
                    parent[v] = parent[parent[v]];
                    v = parent[v];
                }
                return v;
            }
            void Union(int a, int b)
            {
                a = Find(a);
                b = Find(b);
                if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b);
            }
            var weld = new Dictionary<(int, long, long, long, long, long), int>();
            int start = offsets[0];
            for (int v = start; v < vertexCount; v++)
            {
                var p = positions[v];
                var key = (SourceOf(v), Q(p.x), Q(p.y), Q(p.z), Q(uv[v].x * 1000f), Q(uv[v].y * 1000f));
                if (weld.TryGetValue(key, out var other)) Union(v, other);
                else weld[key] = v;
            }
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                if (tris[t] < start) continue;
                Union(tris[t], tris[t + 1]);
                Union(tris[t], tris[t + 2]);
            }
            var byRoot = new Dictionary<int, Chart>();
            var charts = new List<Chart>();
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                if (tris[t] < start) continue;
                int root = Find(tris[t]);
                if (!byRoot.TryGetValue(root, out var c))
                {
                    c = new Chart { Source = SourceOf(tris[t]) };
                    byRoot[root] = c;
                    charts.Add(c);
                }
                c.Triangles.Add(t);
                c.Vertices.Add(tris[t]);
                c.Vertices.Add(tris[t + 1]);
                c.Vertices.Add(tris[t + 2]);
            }
            foreach (var c in charts)
            {
                float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
                foreach (var v in c.Vertices)
                {
                    minX = Math.Min(minX, uv[v].x); minY = Math.Min(minY, uv[v].y);
                    maxX = Math.Max(maxX, uv[v].x); maxY = Math.Max(maxY, uv[v].y);
                }
                c.Bounds = new Rect(minX, minY, Math.Max(0f, maxX - minX), Math.Max(0f, maxY - minY));
            }
            return charts;
        }

        static long Q(float v) => (long)Math.Round(v * 10000.0);

        static double UvArea(List<Vector2> uv, int[] tris, List<int> triangles)
        {
            double a = 0;
            foreach (var t in triangles)
            {
                Vector2 p = uv[tris[t]], q = uv[tris[t + 1]], r = uv[tris[t + 2]];
                a += Math.Abs((q.x - p.x) * (r.y - p.y) - (r.x - p.x) * (q.y - p.y)) * 0.5;
            }
            return a;
        }

        /// <summary>島の中で 2 枚以上の三角形が重なっている画素の割合（島の範囲を 64 画素で測る）。</summary>
        static float SelfOverlap(List<Vector2> uv, int[] tris, Chart c)
        {
            float extent = Math.Max(c.Bounds.width, c.Bounds.height);
            if (extent < 1e-9f || c.Triangles.Count < 2) return 0f;
            const int n = 64;
            float k = n / extent;
            var count = new byte[n * n];
            foreach (var t in c.Triangles)
            {
                Vector2 P(int i) => (uv[tris[t + i]] - c.Bounds.position) * k;
                RasterizeCenters(P(0), P(1), P(2), n, i => { if (count[i] < 255) count[i]++; });
            }
            int covered = 0, multi = 0;
            foreach (var v in count)
            {
                if (v > 0) covered++;
                if (v > 1) multi++;
            }
            return covered == 0 ? 0f : (float)multi / covered;
        }

        /// <summary>画素の中心が三角形の内側にある画素（隣の三角形と共有する辺の上は片方だけに入るよう、境界は半開区間）。</summary>
        static void RasterizeCenters(Vector2 a, Vector2 b, Vector2 c, int n, Action<int> visit)
        {
            float area = (b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y);
            if (Math.Abs(area) < 1e-9f) return;
            int x0 = Math.Max(0, (int)Math.Floor(Math.Min(a.x, Math.Min(b.x, c.x)))), x1 = Math.Min(n - 1, (int)Math.Ceiling(Math.Max(a.x, Math.Max(b.x, c.x))));
            int y0 = Math.Max(0, (int)Math.Floor(Math.Min(a.y, Math.Min(b.y, c.y)))), y1 = Math.Min(n - 1, (int)Math.Ceiling(Math.Max(a.y, Math.Max(b.y, c.y))));
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float px = x + 0.5f, py = y + 0.5f;
                    float w0 = ((b.x - px) * (c.y - py) - (c.x - px) * (b.y - py)) / area;
                    float w1 = ((c.x - px) * (a.y - py) - (a.x - px) * (c.y - py)) / area;
                    float w2 = 1 - w0 - w1;
                    if (w0 <= 0 || w1 <= 0 || w2 <= 0) continue;
                    visit(y * n + x);
                }
        }

        /// <summary>三角形を半テクセル広げて塗る（バイリニアで読まれる範囲）。</summary>
        static void RasterizeConservative(Vector2 a, Vector2 b, Vector2 c, int n, Action<int, int> visit)
        {
            float area = (b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y);
            if (Math.Abs(area) < 1e-9f) return;
            if (area < 0) (b, c) = (c, b);
            const float grow = 0.5f;
            int x0 = Math.Max(0, (int)Math.Floor(Math.Min(a.x, Math.Min(b.x, c.x)) - grow)), x1 = Math.Min(n - 1, (int)Math.Ceiling(Math.Max(a.x, Math.Max(b.x, c.x)) + grow));
            int y0 = Math.Max(0, (int)Math.Floor(Math.Min(a.y, Math.Min(b.y, c.y)) - grow)), y1 = Math.Min(n - 1, (int)Math.Ceiling(Math.Max(a.y, Math.Max(b.y, c.y)) + grow));
            // 各辺の内側への距離（正規化）が -grow 以上なら塗る
            float Edge(Vector2 p, Vector2 q, float x, float y)
            {
                var d = q - p;
                float len = d.magnitude;
                return len < 1e-9f ? 0f : (d.x * (y - p.y) - d.y * (x - p.x)) / len;
            }
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float px = x + 0.5f, py = y + 0.5f;
                    if (Edge(a, b, px, py) < -grow || Edge(b, c, px, py) < -grow || Edge(c, a, px, py) < -grow) continue;
                    visit(x, y);
                }
        }

        static double WorldArea(Mesh mesh)
        {
            var positions = new List<Vector3>();
            mesh.GetVertices(positions);
            double area = 0;
            var tris = Triangles(mesh);
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                var a = positions[tris[t]];
                area += Vector3.Cross(positions[tris[t + 1]] - a, positions[tris[t + 2]] - a).magnitude * 0.5;
            }
            return area;
        }
    }

    /// <summary>
    /// 島の矩形を、縦横比を保った一律の倍率で正方形に詰める（Skyline Bottom-Left・90 度回転あり）。
    /// 島の数が多くても速いよう、MaxRects ではなく Skyline を使う。
    /// </summary>
    public static class ChartPacker
    {
        public sealed class Result
        {
            public float Scale;
            public RectInt[] Rects;
            public bool[] Rotated;
            /// <summary>島そのもの（余白を除く）がキャンバスに占める割合。</summary>
            public float ContentRatio;
        }

        public static Result Pack(IList<Vector2> sizes, int padding, int canvas)
        {
            double sumArea = sizes.Sum(s => (double)s.x * s.y);
            float lo = 0f, hi = (float)Math.Sqrt((double)canvas * canvas / Math.Max(1e-12, sumArea));
            Result best = null;
            for (int iter = 0; iter < 20; iter++)
            {
                float mid = (lo + hi) / 2f;
                var r = TryPack(sizes, padding, canvas, mid);
                if (r != null)
                {
                    best = r;
                    lo = mid;
                }
                else hi = mid;
            }
            if (best == null)
            {
                // 余白だけで埋まるほど島が多い。余白を詰めて最小の倍率で並べる
                for (int p = padding - 1; p >= 0 && best == null; p--)
                    for (float s = hi; s > 1e-6f && best == null; s *= 0.5f) best = TryPack(sizes, p, canvas, s);
                if (best == null) throw new InvalidOperationException("ライトマップ UV の島を並べられません（島が多すぎます）。");
            }
            best.ContentRatio = (float)(sumArea * best.Scale * best.Scale / ((double)canvas * canvas));
            return best;
        }

        struct Segment
        {
            public int X, Y, Width;
        }

        static Result TryPack(IList<Vector2> sizes, int padding, int canvas, float scale)
        {
            var dims = sizes.Select(s => new Vector2Int(
                Math.Max(1, (int)Math.Ceiling(s.x * scale)) + padding * 2,
                Math.Max(1, (int)Math.Ceiling(s.y * scale)) + padding * 2)).ToList();
            if (dims.Any(d => Math.Min(d.x, d.y) > canvas)) return null;
            var order = Enumerable.Range(0, dims.Count)
                .OrderByDescending(i => Math.Max(dims[i].x, dims[i].y))
                .ThenByDescending(i => (long)dims[i].x * dims[i].y)
                .ThenBy(i => i).ToList();
            var skyline = new List<Segment> { new Segment { X = 0, Y = 0, Width = canvas } };
            var result = new Result { Scale = scale, Rects = new RectInt[dims.Count], Rotated = new bool[dims.Count] };
            foreach (var i in order)
            {
                int bestTop = int.MaxValue, bestWaste = int.MaxValue, bestIndex = -1, bestY = 0;
                bool bestRot = false;
                for (int rot = 0; rot < 2; rot++)
                {
                    int w = rot == 0 ? dims[i].x : dims[i].y, h = rot == 0 ? dims[i].y : dims[i].x;
                    if (rot == 1 && w == h) break;
                    for (int s = 0; s < skyline.Count; s++)
                    {
                        if (skyline[s].X + w > canvas) break;
                        int y = 0, waste = 0, remaining = w;
                        for (int j = s; j < skyline.Count && remaining > 0; j++)
                        {
                            y = Math.Max(y, skyline[j].Y);
                            remaining -= skyline[j].Width;
                        }
                        if (y + h > canvas) continue;
                        remaining = w;
                        for (int j = s; j < skyline.Count && remaining > 0; j++)
                        {
                            int span = Math.Min(remaining, skyline[j].Width);
                            waste += (y - skyline[j].Y) * span;
                            remaining -= span;
                        }
                        if (y + h < bestTop || (y + h == bestTop && waste < bestWaste))
                        {
                            bestTop = y + h;
                            bestWaste = waste;
                            bestIndex = s;
                            bestY = y;
                            bestRot = rot == 1;
                        }
                    }
                }
                if (bestIndex < 0) return null;
                int pw = bestRot ? dims[i].y : dims[i].x, ph = bestRot ? dims[i].x : dims[i].y;
                int px = skyline[bestIndex].X;
                result.Rects[i] = new RectInt(px + padding, bestY + padding, pw - padding * 2, ph - padding * 2);
                result.Rotated[i] = bestRot;
                Place(skyline, bestIndex, px, pw, bestY + ph);
            }
            return result;
        }

        static void Place(List<Segment> skyline, int index, int x, int width, int top)
        {
            int end = x + width;
            var replaced = new List<Segment> { new Segment { X = x, Y = top, Width = width } };
            int j = index;
            while (j < skyline.Count && skyline[j].X < end)
            {
                var seg = skyline[j];
                int segEnd = seg.X + seg.Width;
                if (segEnd > end) replaced.Add(new Segment { X = end, Y = seg.Y, Width = segEnd - end });
                j++;
            }
            skyline.RemoveRange(index, j - index);
            skyline.InsertRange(index, replaced);
            // 同じ高さの隣り合う区間をまとめる
            for (int k = skyline.Count - 1; k > 0; k--)
                if (skyline[k].Y == skyline[k - 1].Y)
                {
                    var merged = skyline[k - 1];
                    merged.Width += skyline[k].Width;
                    skyline[k - 1] = merged;
                    skyline.RemoveAt(k);
                }
        }
    }
}
