using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace ChikumaTateshina.UnityAssetsOptimizer
{
    public sealed class DecimationResult
    {
        public Mesh Mesh;
        public long SourceTriangles;
        public long ResultTriangles;
        /// <summary>採用した縮約の最大誤差（二次誤差の平方根、メッシュ単位）。</summary>
        public double MaxError;
        public double ErrorLimit;
    }

    /// <summary>
    /// 二次誤差（QEM）に基づく half-edge collapse による簡略化。
    /// 頂点を既存頂点へ寄せるだけで新しい頂点属性を作らないため、UV・法線・ボーンウェイト・BlendShape は残った頂点の値をそのまま保持する。
    /// 境界辺・UV 等の継ぎ目（頂点が分かれている辺）・複数サブメッシュにまたがる頂点は固定する。
    /// 許容誤差（境界ボックス対角長 × 比率）を超える縮約は行わない。
    /// 形が変わらない平面でも、消える頂点の UV が周囲から線形に決まらなければテクスチャが歪むため、
    /// 縮約前後の UV（UV0 と、あれば UV1）の差が許容値（UV 単位）を超える縮約も行わない。
    /// </summary>
    public static class MeshDecimator
    {
        struct HeapEntry
        {
            public double Cost;
            public int U, V, StampU, StampV;
        }

        /// <summary>簡略化の入力と結果。Run は Unity API を使わないため別スレッドで実行できる。</summary>
        public sealed class Job
        {
            internal int VertexCount;
            internal int SubMeshCount;
            internal List<Vector3> Positions;
            internal List<int> Tris;
            internal List<int> TriSub;
            internal Bounds Bounds;
            internal float Ratio;
            internal float MaxErrorRelative;
            /// <summary>UV0 / UV1（ない場合は null）。</summary>
            internal List<Vector2>[] Uvs;
            internal float MaxUvError;
            internal int RejectedByUv;
            internal List<int>[] PerSub;
            internal long SourceTriangles;
            internal long ResultTriangles;
            internal double MaxApplied;
            internal double Limit;
        }

        /// <summary>UV の許容誤差（UV 単位）。4096 のアトラスで約 2 テクセル、1024 のテクスチャで約 0.5 テクセル。</summary>
        public const float DefaultMaxUvError = 0.0005f;

        public static DecimationResult Decimate(Mesh src, float ratio, float maxErrorRelative)
        {
            var job = Prepare(src, ratio, maxErrorRelative);
            Run(job);
            return Complete(src, job);
        }

        /// <summary>計算部分を別スレッドで実行するコルーチン。</summary>
        public static IEnumerator DecimateRoutine(Mesh src, float ratio, float maxErrorRelative, Action<DecimationResult> done)
        {
            var job = Prepare(src, ratio, maxErrorRelative);
            yield return Task.Run(() => Run(job));
            done(Complete(src, job));
        }

        /// <summary>メッシュからデータを読み出す（メインスレッド）。</summary>
        public static Job Prepare(Mesh src, float ratio, float maxErrorRelative)
        {
            var positions = new List<Vector3>();
            src.GetVertices(positions);
            var tris = new List<int>();
            var triSub = new List<int>();
            for (int s = 0; s < src.subMeshCount; s++)
            {
                if (src.GetTopology(s) != MeshTopology.Triangles) throw new InvalidOperationException("三角形以外のトポロジーは未対応です。");
                var t = src.GetTriangles(s, true);
                tris.AddRange(t);
                for (int i = 0; i < t.Length / 3; i++) triSub.Add(s);
            }
            return new Job
            {
                VertexCount = src.vertexCount,
                SubMeshCount = src.subMeshCount,
                Positions = positions,
                Tris = tris,
                TriSub = triSub,
                Bounds = src.bounds,
                Ratio = ratio,
                MaxErrorRelative = maxErrorRelative,
                Uvs = Enumerable.Range(0, 2).Select(ch =>
                {
                    if (!src.HasVertexAttribute(VertexAttribute.TexCoord0 + ch)) return null;
                    var uv = new List<Vector2>();
                    src.GetUVs(ch, uv);
                    return uv.Count == positions.Count ? uv : null;
                }).Where(u => u != null).ToArray(),
                MaxUvError = DefaultMaxUvError,
            };
        }

        /// <summary>結果のメッシュを作る（メインスレッド）。</summary>
        public static DecimationResult Complete(Mesh src, Job job)
        {
            return new DecimationResult
            {
                Mesh = BuildSubset(src, job.PerSub, src.name + "_reduced"),
                SourceTriangles = job.SourceTriangles,
                ResultTriangles = job.ResultTriangles,
                MaxError = Math.Sqrt(job.MaxApplied),
                ErrorLimit = job.Limit,
            };
        }

        /// <summary>簡略化の計算本体。Unity API を使わない。</summary>
        public static void Run(Job job)
        {
            int vc = job.VertexCount;
            var positions = job.Positions;
            var tris = job.Tris;
            var triSub = job.TriSub;
            float ratio = job.Ratio;
            float maxErrorRelative = job.MaxErrorRelative;
            int triCount = tris.Count / 3;
            var alive = Enumerable.Repeat(true, triCount).ToArray();
            var vertexTris = new List<int>[vc];
            for (int v = 0; v < vc; v++) vertexTris[v] = new List<int>();
            for (int t = 0; t < triCount; t++)
                for (int k = 0; k < 3; k++) vertexTris[tris[t * 3 + k]].Add(t);

            // 固定頂点: 境界辺・継ぎ目（同一 index で共有されない辺）・サブメッシュ境界
            var locked = new bool[vc];
            var edgeUse = new Dictionary<long, int>();
            for (int t = 0; t < triCount; t++)
                for (int k = 0; k < 3; k++)
                {
                    var key = EdgeKey(tris[t * 3 + k], tris[t * 3 + (k + 1) % 3]);
                    edgeUse.TryGetValue(key, out int c);
                    edgeUse[key] = c + 1;
                }
            foreach (var pair in edgeUse)
                if (pair.Value != 2)
                {
                    locked[(int)(pair.Key >> 32)] = true;
                    locked[(int)(pair.Key & 0xffffffff)] = true;
                }
            for (int v = 0; v < vc; v++)
                if (vertexTris[v].Select(t => triSub[t]).Distinct().Count() > 1) locked[v] = true;

            // 二次誤差行列（対称 4x4 の上三角 10 要素）
            var q = new double[vc * 10];
            for (int t = 0; t < triCount; t++)
            {
                var a = positions[tris[t * 3]];
                var b = positions[tris[t * 3 + 1]];
                var c = positions[tris[t * 3 + 2]];
                var n = Vector3.Cross(b - a, c - a);
                double area = n.magnitude;
                if (area < 1e-12) continue;
                n /= (float)area;
                double d = -Vector3.Dot(n, a);
                double[] plane = { n.x, n.y, n.z, d };
                for (int k = 0; k < 3; k++) AddPlane(q, tris[t * 3 + k], plane, area);
            }

            var bounds = job.Bounds;
            double limit = maxErrorRelative * bounds.size.magnitude;
            double limitSq = limit * limit;
            var stamp = new int[vc];
            var dead = new bool[vc];
            var heap = new List<HeapEntry>();

            void PushEdges(int v)
            {
                foreach (var n in Neighbors(v, vertexTris, tris, alive))
                {
                    if (!locked[n]) Push(heap, Entry(n, v));
                    if (!locked[v]) Push(heap, Entry(v, n));
                }
            }

            HeapEntry Entry(int u, int v)
            {
                double cost = Evaluate(q, u, v, positions[v]);
                return new HeapEntry { Cost = cost, U = u, V = v, StampU = stamp[u], StampV = stamp[v] };
            }

            for (int v = 0; v < vc; v++) if (!locked[v]) PushEdges(v);

            long target = Math.Max(1, (long)Math.Floor(triCount * ratio));
            long current = triCount;
            double maxApplied = 0;
            while (current > target && heap.Count > 0)
            {
                var e = Pop(heap);
                if (dead[e.U] || dead[e.V] || e.StampU != stamp[e.U] || e.StampV != stamp[e.V]) continue;
                if (e.Cost > limitSq) break;
                if (!CanCollapse(e.U, e.V, positions, vertexTris, tris, alive)) continue;
                if (job.Uvs.Length > 0 && !UvPreserved(e.U, e.V, positions, job.Uvs, job.MaxUvError, vertexTris, tris, alive))
                {
                    job.RejectedByUv++;
                    continue;
                }

                // u を v に寄せる
                foreach (var t in vertexTris[e.U].ToList())
                {
                    if (!alive[t]) continue;
                    bool hasV = tris[t * 3] == e.V || tris[t * 3 + 1] == e.V || tris[t * 3 + 2] == e.V;
                    if (hasV)
                    {
                        alive[t] = false;
                        current--;
                        for (int k = 0; k < 3; k++) vertexTris[tris[t * 3 + k]].Remove(t);
                    }
                    else
                    {
                        for (int k = 0; k < 3; k++) if (tris[t * 3 + k] == e.U) tris[t * 3 + k] = e.V;
                        vertexTris[e.V].Add(t);
                    }
                }
                vertexTris[e.U].Clear();
                dead[e.U] = true;
                for (int i = 0; i < 10; i++) q[e.V * 10 + i] += q[e.U * 10 + i];
                stamp[e.V]++;
                maxApplied = Math.Max(maxApplied, e.Cost);
                foreach (var n in Neighbors(e.V, vertexTris, tris, alive)) stamp[n]++;
                PushEdges(e.V);
                foreach (var n in Neighbors(e.V, vertexTris, tris, alive)) PushEdges(n);
            }

            var perSub = new List<int>[job.SubMeshCount];
            for (int s = 0; s < perSub.Length; s++) perSub[s] = new List<int>();
            for (int t = 0; t < triCount; t++)
                if (alive[t]) perSub[triSub[t]].AddRange(new[] { tris[t * 3], tris[t * 3 + 1], tris[t * 3 + 2] });

            job.PerSub = perSub;
            job.SourceTriangles = triCount;
            job.ResultTriangles = current;
            job.MaxApplied = maxApplied;
            job.Limit = limit;
        }

        static long EdgeKey(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

        static void AddPlane(double[] q, int v, double[] p, double w)
        {
            int o = v * 10;
            q[o + 0] += w * p[0] * p[0]; q[o + 1] += w * p[0] * p[1]; q[o + 2] += w * p[0] * p[2]; q[o + 3] += w * p[0] * p[3];
            q[o + 4] += w * p[1] * p[1]; q[o + 5] += w * p[1] * p[2]; q[o + 6] += w * p[1] * p[3];
            q[o + 7] += w * p[2] * p[2]; q[o + 8] += w * p[2] * p[3];
            q[o + 9] += w * p[3] * p[3];
        }

        /// <summary>(Q_u + Q_v) を位置 p で評価した誤差（面積重み付き二乗距離の和）。</summary>
        static double Evaluate(double[] q, int u, int v, Vector3 p)
        {
            double x = p.x, y = p.y, z = p.z;
            double r = 0;
            foreach (var o in new[] { u * 10, v * 10 })
            {
                r += q[o + 0] * x * x + 2 * q[o + 1] * x * y + 2 * q[o + 2] * x * z + 2 * q[o + 3] * x
                     + q[o + 4] * y * y + 2 * q[o + 5] * y * z + 2 * q[o + 6] * y
                     + q[o + 7] * z * z + 2 * q[o + 8] * z + q[o + 9];
            }
            // 面積重みを正規化（誤差を距離の二乗として扱う）
            double w = 0;
            return Math.Max(0, r) / Math.Max(1e-12, AreaWeight(q, u) + AreaWeight(q, v) + w);
        }

        static double AreaWeight(double[] q, int v)
        {
            // 平面ベクトルは単位法線なので Σw·(nx²+ny²+nz²) = Σw
            int o = v * 10;
            return q[o + 0] + q[o + 4] + q[o + 7];
        }

        static HashSet<int> Neighbors(int v, List<int>[] vertexTris, List<int> tris, bool[] alive)
        {
            var set = new HashSet<int>();
            foreach (var t in vertexTris[v])
            {
                if (!alive[t]) continue;
                for (int k = 0; k < 3; k++)
                {
                    var n = tris[t * 3 + k];
                    if (n != v) set.Add(n);
                }
            }
            return set;
        }

        /// <summary>多様体性（リンク条件）と面の反転を確認する。</summary>
        static bool CanCollapse(int u, int v, List<Vector3> positions, List<int>[] vertexTris, List<int> tris, bool[] alive)
        {
            var nu = Neighbors(u, vertexTris, tris, alive);
            if (!nu.Contains(v)) return false;
            var nv = Neighbors(v, vertexTris, tris, alive);
            int common = nu.Count(nv.Contains);
            if (common != 2) return false;

            foreach (var t in vertexTris[u])
            {
                if (!alive[t]) continue;
                int a = tris[t * 3], b = tris[t * 3 + 1], c = tris[t * 3 + 2];
                if (a == v || b == v || c == v) continue;
                var before = Normal(positions[a], positions[b], positions[c]);
                var pa = a == u ? positions[v] : positions[a];
                var pb = b == u ? positions[v] : positions[b];
                var pc = c == u ? positions[v] : positions[c];
                var after = Normal(pa, pb, pc);
                if (after == Vector3.zero || Vector3.Dot(before, after) < 0.2f) return false;
            }
            return true;
        }

        /// <summary>
        /// u を v に寄せたときのテクスチャの歪み: u の位置・u を含む三角形の重心・u から出る辺の中点で、
        /// 縮約前の UV と縮約後の三角形で補間した UV の差がすべて許容値以内か。
        /// </summary>
        static bool UvPreserved(int u, int v, List<Vector3> positions, List<Vector2>[] uvs, float tolerance, List<int>[] vertexTris, List<int> tris, bool[] alive)
        {
            var before = vertexTris[u].Where(t => alive[t]).ToList();
            var after = before.Where(t => tris[t * 3] != v && tris[t * 3 + 1] != v && tris[t * 3 + 2] != v).ToList();
            if (after.Count == 0) return false;
            int Moved(int i) => i == u ? v : i;

            foreach (var t in before)
            {
                int a = tris[t * 3], b = tris[t * 3 + 1], c = tris[t * 3 + 2];
                // 縮約前の三角形上のサンプル点（重心座標）
                var samples = new[] { new Vector3(1f / 3, 1f / 3, 1f / 3), Corner(a, b, c, u), Mid(a, b, c, u, 0), Mid(a, b, c, u, 1) };
                foreach (var w in samples)
                {
                    if (w.x < 0) continue;
                    var p = positions[a] * w.x + positions[b] * w.y + positions[c] * w.z;
                    // 縮約後の三角形のうち、p を最もよく含むもので補間する
                    int best = -1;
                    Vector3 bestW = default;
                    float bestScore = float.NegativeInfinity;
                    foreach (var n in after)
                    {
                        var bw = Barycentric(p, positions[Moved(tris[n * 3])], positions[Moved(tris[n * 3 + 1])], positions[Moved(tris[n * 3 + 2])]);
                        float score = Mathf.Min(bw.x, Mathf.Min(bw.y, bw.z));
                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = n;
                            bestW = bw;
                        }
                    }
                    if (best < 0 || bestScore < -0.05f) return false; // 縮約後の面の外（形が大きく変わる）
                    foreach (var uv in uvs)
                    {
                        var old = uv[a] * w.x + uv[b] * w.y + uv[c] * w.z;
                        var now = uv[Moved(tris[best * 3])] * bestW.x + uv[Moved(tris[best * 3 + 1])] * bestW.y + uv[Moved(tris[best * 3 + 2])] * bestW.z;
                        if ((old - now).sqrMagnitude > tolerance * tolerance) return false;
                    }
                }
            }
            return true;
        }

        /// <summary>三角形 (a,b,c) の頂点 u の重心座標（u がなければ x = -1）。</summary>
        static Vector3 Corner(int a, int b, int c, int u) =>
            a == u ? new Vector3(1, 0, 0) : b == u ? new Vector3(0, 1, 0) : c == u ? new Vector3(0, 0, 1) : new Vector3(-1, 0, 0);

        /// <summary>u から出る辺（which = 0 / 1 本目）の中点の重心座標。</summary>
        static Vector3 Mid(int a, int b, int c, int u, int which)
        {
            var corner = Corner(a, b, c, u);
            if (corner.x < 0) return new Vector3(-1, 0, 0);
            var others = new List<Vector3>();
            if (a != u) others.Add(new Vector3(1, 0, 0));
            if (b != u) others.Add(new Vector3(0, 1, 0));
            if (c != u) others.Add(new Vector3(0, 0, 1));
            return (corner + others[which]) * 0.5f;
        }

        /// <summary>p を三角形の平面へ射影したときの重心座標。退化した三角形では (-1,-1,-1)。</summary>
        static Vector3 Barycentric(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 v0 = b - a, v1 = c - a, v2 = p - a;
            float d00 = Vector3.Dot(v0, v0), d01 = Vector3.Dot(v0, v1), d11 = Vector3.Dot(v1, v1);
            float d20 = Vector3.Dot(v2, v0), d21 = Vector3.Dot(v2, v1);
            float denom = d00 * d11 - d01 * d01;
            if (Mathf.Abs(denom) < 1e-20f) return new Vector3(-1, -1, -1);
            float y = (d11 * d20 - d01 * d21) / denom;
            float z = (d00 * d21 - d01 * d20) / denom;
            return new Vector3(1 - y - z, y, z);
        }

        static Vector3 Normal(Vector3 a, Vector3 b, Vector3 c)
        {
            var n = Vector3.Cross(b - a, c - a);
            return n.sqrMagnitude < 1e-20f ? Vector3.zero : n.normalized;
        }

        static void Push(List<HeapEntry> heap, HeapEntry e)
        {
            heap.Add(e);
            int i = heap.Count - 1;
            while (i > 0)
            {
                int p = (i - 1) / 2;
                if (heap[p].Cost <= heap[i].Cost) break;
                (heap[p], heap[i]) = (heap[i], heap[p]);
                i = p;
            }
        }

        static HeapEntry Pop(List<HeapEntry> heap)
        {
            var top = heap[0];
            var last = heap[heap.Count - 1];
            heap.RemoveAt(heap.Count - 1);
            if (heap.Count == 0) return top;
            heap[0] = last;
            int i = 0;
            while (true)
            {
                int l = i * 2 + 1, r = l + 1, m = i;
                if (l < heap.Count && heap[l].Cost < heap[m].Cost) m = l;
                if (r < heap.Count && heap[r].Cost < heap[m].Cost) m = r;
                if (m == i) break;
                (heap[m], heap[i]) = (heap[i], heap[m]);
                i = m;
            }
            return top;
        }

        /// <summary>使用されている頂点のみを残したメッシュを作る（全属性・ボーン・BlendShape を保持）。</summary>
        public static Mesh BuildSubset(Mesh src, List<int>[] submeshTriangles, string name)
        {
            int vc = src.vertexCount;
            var map = Enumerable.Repeat(-1, vc).ToArray();
            var order = new List<int>();
            foreach (var list in submeshTriangles)
                foreach (var v in list)
                    if (map[v] < 0)
                    {
                        map[v] = order.Count;
                        order.Add(v);
                    }

            var mesh = new Mesh { name = name, indexFormat = order.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            List<T> Pick<T>(List<T> l) => order.Select(i => l[i]).ToList();

            var p = new List<Vector3>();
            src.GetVertices(p);
            mesh.SetVertices(Pick(p));
            if (src.HasVertexAttribute(VertexAttribute.Normal)) { var n = new List<Vector3>(); src.GetNormals(n); mesh.SetNormals(Pick(n)); }
            if (src.HasVertexAttribute(VertexAttribute.Tangent)) { var t = new List<Vector4>(); src.GetTangents(t); mesh.SetTangents(Pick(t)); }
            if (src.HasVertexAttribute(VertexAttribute.Color))
            {
                if (src.GetVertexAttributeFormat(VertexAttribute.Color) == VertexAttributeFormat.UNorm8) { var c = new List<Color32>(); src.GetColors(c); mesh.SetColors(Pick(c)); }
                else { var c = new List<Color>(); src.GetColors(c); mesh.SetColors(Pick(c)); }
            }
            for (int ch = 0; ch < 8; ch++)
            {
                var attr = VertexAttribute.TexCoord0 + ch;
                if (!src.HasVertexAttribute(attr)) continue;
                int dim = src.GetVertexAttributeDimension(attr);
                if (dim <= 2) { var u = new List<Vector2>(); src.GetUVs(ch, u); mesh.SetUVs(ch, Pick(u)); }
                else if (dim == 3) { var u = new List<Vector3>(); src.GetUVs(ch, u); mesh.SetUVs(ch, Pick(u)); }
                else { var u = new List<Vector4>(); src.GetUVs(ch, u); mesh.SetUVs(ch, Pick(u)); }
            }
            mesh.subMeshCount = submeshTriangles.Length;
            for (int s = 0; s < submeshTriangles.Length; s++)
                mesh.SetTriangles(submeshTriangles[s].Select(v => map[v]).ToList(), s, false);

            var bpv = src.GetBonesPerVertex();
            if (bpv.Length > 0)
            {
                var weights = src.GetAllBoneWeights();
                var start = new int[vc];
                for (int i = 1; i < vc; i++) start[i] = start[i - 1] + bpv[i - 1];
                var counts = new byte[order.Count];
                var outWeights = new List<BoneWeight1>();
                for (int i = 0; i < order.Count; i++)
                {
                    int s = order[i];
                    counts[i] = bpv[s];
                    for (int j = 0; j < bpv[s]; j++) outWeights.Add(weights[start[s] + j]);
                }
                var na = new NativeArray<byte>(counts, Allocator.Temp);
                var nw = new NativeArray<BoneWeight1>(outWeights.ToArray(), Allocator.Temp);
                try { mesh.SetBoneWeights(na, nw); }
                finally { na.Dispose(); nw.Dispose(); }
                mesh.bindposes = src.bindposes;
            }

            var dv = new Vector3[vc];
            var dn = new Vector3[vc];
            var dt = new Vector3[vc];
            for (int shape = 0; shape < src.blendShapeCount; shape++)
                for (int f = 0; f < src.GetBlendShapeFrameCount(shape); f++)
                {
                    src.GetBlendShapeFrameVertices(shape, f, dv, dn, dt);
                    mesh.AddBlendShapeFrame(src.GetBlendShapeName(shape), src.GetBlendShapeFrameWeight(shape, f),
                        order.Select(i => dv[i]).ToArray(), order.Select(i => dn[i]).ToArray(), order.Select(i => dt[i]).ToArray());
                }
            mesh.RecalculateBounds();
            mesh.bounds = src.bounds;
            return mesh;
        }
    }
}
