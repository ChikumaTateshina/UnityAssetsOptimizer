using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace ChikumaTateshina.UnityAssetsOptimizer
{
    public sealed class MergeSource
    {
        public Mesh Mesh;
        public Material[] Materials;
        /// <summary>静的結合: ソースのメッシュ空間 → 結合先のメッシュ空間。</summary>
        public Matrix4x4 ToTarget = Matrix4x4.identity;
        /// <summary>スキン結合: ソースのボーン（bindposes と同じ順序）。</summary>
        public Transform[] Bones;
        /// <summary>スキン結合: ソース Renderer の BlendShape ウェイト。</summary>
        public float[] BlendShapeWeights;
    }

    public sealed class MergeResult
    {
        public Mesh Mesh;
        public Material[] Materials;
        public Transform[] Bones;
        public float[] BlendShapeWeights;
        /// <summary>各ソースの頂点の開始位置（結合後のメッシュ内）。</summary>
        public int[] VertexOffsets;
    }

    /// <summary>
    /// 複数メッシュを1つに結合する。静的結合は頂点を結合先空間へ変換し、スキン結合は頂点をそのまま
    /// 各ソースの bindpose と組にして保持する（スキニング結果は Renderer の Transform に依存しないため）。
    /// 同じ Material のサブメッシュは1つにまとめる。元 Mesh は変更しない。
    /// </summary>
    public static class MeshMerger
    {
        static readonly VertexAttribute[] OptionalAttributes =
        {
            VertexAttribute.Normal, VertexAttribute.Tangent, VertexAttribute.Color,
            VertexAttribute.TexCoord0, VertexAttribute.TexCoord1, VertexAttribute.TexCoord2, VertexAttribute.TexCoord3,
            VertexAttribute.TexCoord4, VertexAttribute.TexCoord5, VertexAttribute.TexCoord6, VertexAttribute.TexCoord7,
        };

        /// <summary>結合可能な頂点形式か判定するためのシグネチャ（属性の有無・次元・色形式）。</summary>
        public static string AttributeSignature(Mesh m)
        {
            var parts = new List<string>();
            foreach (var a in OptionalAttributes)
            {
                if (!m.HasVertexAttribute(a)) continue;
                parts.Add(a + ":" + m.GetVertexAttributeDimension(a) + (a == VertexAttribute.Color ? ":" + m.GetVertexAttributeFormat(a) : string.Empty));
            }
            return string.Join(",", parts);
        }

        public static MergeResult Merge(IList<MergeSource> sources, bool skinned, string name)
        {
            if (sources.Count < 2) throw new ArgumentException("結合には2つ以上のメッシュが必要です。");
            var sig = AttributeSignature(sources[0].Mesh);
            if (sources.Any(s => AttributeSignature(s.Mesh) != sig)) throw new InvalidOperationException("頂点形式が一致しません。");
            var first = sources[0].Mesh;

            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var tangents = new List<Vector4>();
            var colors32 = new List<Color32>();
            var colors = new List<Color>();
            var uvs = new List<Vector4>[8];
            var uvDims = new int[8];
            for (int ch = 0; ch < 8; ch++)
            {
                var attr = VertexAttribute.TexCoord0 + ch;
                if (first.HasVertexAttribute(attr))
                {
                    uvs[ch] = new List<Vector4>();
                    uvDims[ch] = first.GetVertexAttributeDimension(attr);
                }
            }
            bool hasNormals = first.HasVertexAttribute(VertexAttribute.Normal);
            bool hasTangents = first.HasVertexAttribute(VertexAttribute.Tangent);
            bool hasColors = first.HasVertexAttribute(VertexAttribute.Color);
            bool color32 = hasColors && first.GetVertexAttributeFormat(VertexAttribute.Color) == VertexAttributeFormat.UNorm8;

            var materialOrder = new List<Material>();
            var trianglesByMaterial = new Dictionary<Material, List<int>>();
            var bones = new List<Transform>();
            var bindposes = new List<Matrix4x4>();
            var boneCounts = new List<byte>();
            var boneWeights = new List<BoneWeight1>();
            var shapeNames = new List<string>();
            var shapeOwner = new List<int>();
            var shapeIndex = new List<int>();
            var weights = new List<float>();
            var vertexOffsets = new int[sources.Count];

            for (int si = 0; si < sources.Count; si++)
            {
                var src = sources[si];
                var mesh = src.Mesh;
                int offset = positions.Count;
                vertexOffsets[si] = offset;
                var m = src.ToTarget;
                bool mirrored = !skinned && m.determinant < 0;
                var normalMatrix = m.inverse.transpose;

                var p = new List<Vector3>();
                mesh.GetVertices(p);
                positions.AddRange(skinned ? p : p.Select(v => m.MultiplyPoint3x4(v)));
                if (hasNormals)
                {
                    var n = new List<Vector3>();
                    mesh.GetNormals(n);
                    normals.AddRange(skinned ? n : n.Select(v => normalMatrix.MultiplyVector(v).normalized));
                }
                if (hasTangents)
                {
                    var t = new List<Vector4>();
                    mesh.GetTangents(t);
                    tangents.AddRange(skinned ? t : t.Select(v =>
                    {
                        var d = m.MultiplyVector(new Vector3(v.x, v.y, v.z)).normalized;
                        return new Vector4(d.x, d.y, d.z, mirrored ? -v.w : v.w);
                    }));
                }
                if (hasColors)
                {
                    if (color32)
                    {
                        var c = new List<Color32>();
                        mesh.GetColors(c);
                        colors32.AddRange(c);
                    }
                    else
                    {
                        var c = new List<Color>();
                        mesh.GetColors(c);
                        colors.AddRange(c);
                    }
                }
                for (int ch = 0; ch < 8; ch++)
                {
                    if (uvs[ch] == null) continue;
                    var u = new List<Vector4>();
                    mesh.GetUVs(ch, u);
                    uvs[ch].AddRange(u);
                }

                int slots = Math.Min(mesh.subMeshCount, src.Materials.Length);
                for (int s = 0; s < slots; s++)
                {
                    var mat = src.Materials[s];
                    if (mat == null) throw new InvalidOperationException("Material 未設定のスロットは結合できません。");
                    if (!trianglesByMaterial.TryGetValue(mat, out var list))
                    {
                        list = new List<int>();
                        trianglesByMaterial[mat] = list;
                        materialOrder.Add(mat);
                    }
                    var tris = mesh.GetTriangles(s, true);
                    for (int i = 0; i < tris.Length; i += 3)
                    {
                        if (mirrored)
                        {
                            list.Add(tris[i] + offset);
                            list.Add(tris[i + 2] + offset);
                            list.Add(tris[i + 1] + offset);
                        }
                        else
                        {
                            list.Add(tris[i] + offset);
                            list.Add(tris[i + 1] + offset);
                            list.Add(tris[i + 2] + offset);
                        }
                    }
                }

                if (skinned)
                {
                    var srcBindposes = mesh.bindposes;
                    var map = new int[srcBindposes.Length];
                    for (int b = 0; b < srcBindposes.Length; b++)
                    {
                        int existing = -1;
                        for (int k = 0; k < bones.Count; k++)
                            if (bones[k] == src.Bones[b] && bindposes[k] == srcBindposes[b])
                            {
                                existing = k;
                                break;
                            }
                        if (existing < 0)
                        {
                            existing = bones.Count;
                            bones.Add(src.Bones[b]);
                            bindposes.Add(srcBindposes[b]);
                        }
                        map[b] = existing;
                    }
                    var bpv = mesh.GetBonesPerVertex();
                    var bw = mesh.GetAllBoneWeights();
                    for (int v = 0; v < bpv.Length; v++) boneCounts.Add(bpv[v]);
                    for (int w = 0; w < bw.Length; w++)
                    {
                        var weight = bw[w];
                        weight.boneIndex = map[weight.boneIndex];
                        boneWeights.Add(weight);
                    }
                    for (int b = 0; b < mesh.blendShapeCount; b++)
                    {
                        shapeNames.Add(mesh.GetBlendShapeName(b));
                        shapeOwner.Add(si);
                        shapeIndex.Add(b);
                        weights.Add(src.BlendShapeWeights != null && b < src.BlendShapeWeights.Length ? src.BlendShapeWeights[b] : 0f);
                    }
                }
            }

            if (shapeNames.Count != shapeNames.Distinct().Count()) throw new InvalidOperationException("BlendShape 名が重複しています。");

            int total = positions.Count;
            var result = new Mesh { name = name, indexFormat = total > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            result.SetVertices(positions);
            if (hasNormals) result.SetNormals(normals);
            if (hasTangents) result.SetTangents(tangents);
            if (hasColors)
            {
                if (color32) result.SetColors(colors32);
                else result.SetColors(colors);
            }
            for (int ch = 0; ch < 8; ch++)
            {
                if (uvs[ch] == null) continue;
                if (uvDims[ch] <= 2) result.SetUVs(ch, uvs[ch].Select(v => (Vector2)v).ToList());
                else if (uvDims[ch] == 3) result.SetUVs(ch, uvs[ch].Select(v => (Vector3)v).ToList());
                else result.SetUVs(ch, uvs[ch]);
            }
            result.subMeshCount = materialOrder.Count;
            for (int i = 0; i < materialOrder.Count; i++) result.SetTriangles(trianglesByMaterial[materialOrder[i]], i, false);

            if (skinned)
            {
                var naCounts = new NativeArray<byte>(boneCounts.ToArray(), Allocator.Temp);
                var naWeights = new NativeArray<BoneWeight1>(boneWeights.ToArray(), Allocator.Temp);
                try
                {
                    result.SetBoneWeights(naCounts, naWeights);
                }
                finally
                {
                    naCounts.Dispose();
                    naWeights.Dispose();
                }
                result.bindposes = bindposes.ToArray();

                for (int k = 0; k < shapeNames.Count; k++)
                {
                    var srcMesh = sources[shapeOwner[k]].Mesh;
                    int start = vertexOffsets[shapeOwner[k]];
                    int count = srcMesh.vertexCount;
                    for (int f = 0; f < srcMesh.GetBlendShapeFrameCount(shapeIndex[k]); f++)
                    {
                        var dv = new Vector3[count];
                        var dn = new Vector3[count];
                        var dt = new Vector3[count];
                        srcMesh.GetBlendShapeFrameVertices(shapeIndex[k], f, dv, dn, dt);
                        var fdv = new Vector3[total];
                        var fdn = new Vector3[total];
                        var fdt = new Vector3[total];
                        Array.Copy(dv, 0, fdv, start, count);
                        Array.Copy(dn, 0, fdn, start, count);
                        Array.Copy(dt, 0, fdt, start, count);
                        result.AddBlendShapeFrame(shapeNames[k], srcMesh.GetBlendShapeFrameWeight(shapeIndex[k], f), fdv, hasNormals ? fdn : null, hasTangents ? fdt : null);
                    }
                }
            }
            result.RecalculateBounds();

            return new MergeResult
            {
                Mesh = result,
                Materials = materialOrder.ToArray(),
                Bones = bones.ToArray(),
                BlendShapeWeights = weights.ToArray(),
                VertexOffsets = vertexOffsets,
            };
        }

        /// <summary>
        /// ライトマップ UV（UV1）で三角形が重なっている割合（塗られた画素のうち 2 回以上塗られたもの。256x256 の格子で測る）。
        /// ライトマップ UV として作られたものは 0 に近い。UV1 がなければ 1。
        /// </summary>
        public static float LightmapUvOverlap(Mesh mesh)
        {
            if (!mesh.HasVertexAttribute(VertexAttribute.TexCoord1)) return 1f;
            const int n = 256;
            var uv = new List<Vector2>();
            mesh.GetUVs(1, uv);
            var count = new byte[n * n];
            for (int sm = 0; sm < mesh.subMeshCount; sm++)
            {
                if (mesh.GetTopology(sm) != MeshTopology.Triangles) continue;
                var tris = mesh.GetTriangles(sm);
                for (int t = 0; t + 2 < tris.Length; t += 3)
                {
                    Vector2 a = uv[tris[t]] * n, b = uv[tris[t + 1]] * n, c = uv[tris[t + 2]] * n;
                    float area = (b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y);
                    if (Math.Abs(area) < 1e-6f) continue;
                    int x0 = Math.Max(0, (int)Math.Floor(Math.Min(a.x, Math.Min(b.x, c.x)))), x1 = Math.Min(n - 1, (int)Math.Ceiling(Math.Max(a.x, Math.Max(b.x, c.x))));
                    int y0 = Math.Max(0, (int)Math.Floor(Math.Min(a.y, Math.Min(b.y, c.y)))), y1 = Math.Min(n - 1, (int)Math.Ceiling(Math.Max(a.y, Math.Max(b.y, c.y))));
                    for (int y = y0; y <= y1; y++)
                        for (int x = x0; x <= x1; x++)
                        {
                            float px = x + 0.5f, py = y + 0.5f;
                            float w0 = ((b.x - px) * (c.y - py) - (c.x - px) * (b.y - py)) / area;
                            float w1 = ((c.x - px) * (a.y - py) - (a.x - px) * (c.y - py)) / area;
                            if (w0 < 0 || w1 < 0 || 1 - w0 - w1 < 0) continue;
                            if (count[y * n + x] < 255) count[y * n + x]++;
                        }
                }
            }
            int covered = 0, multi = 0;
            foreach (var c in count)
            {
                if (c > 0) covered++;
                if (c > 1) multi++;
            }
            return covered == 0 ? 1f : (float)multi / covered;
        }

        /// <summary>
        /// 静的結合したメッシュのライトマップ UV（UV1）を、ソースごとに重ならないよう並べ直す（島の形と縦横比は保つ。<see cref="LightmapUvLayout.Pack"/>）。
        /// UV1 がない場合は Unity のライトマップ UV 自動生成で作る。そのままだと各ソースの UV1 が重なり、ベイクした光が混ざる。
        /// </summary>
        public static void PrepareLightmapUvs(Mesh mesh, int[] vertexOffsets) => LightmapUvLayout.Pack(mesh, vertexOffsets);
    }
}
