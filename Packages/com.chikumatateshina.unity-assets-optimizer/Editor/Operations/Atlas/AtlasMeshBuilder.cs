using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace ChikumaTateshina.AssetsOptimizer
{
    public struct SubmeshMapping
    {
        /// <summary>0 = UV 変換なし。それ以外は UV 変換の識別子（同じ識別子は同じ Offset/Scale）。</summary>
        public int TransformId;
        public Vector2 Offset;
        public Vector2 Scale;
        /// <summary>再構成後のサブメッシュ（Material スロット）番号。</summary>
        public int TargetSlot;
    }

    /// <summary>
    /// 元 Mesh を変更せずに、UV0 を変換しサブメッシュを統合した新しい Mesh を作る。
    /// 異なる UV 変換を受けるサブメッシュ間で共有される頂点は複製し、全頂点属性・ボーンウェイト・BlendShape を保持する。
    /// </summary>
    public static class AtlasMeshBuilder
    {
        public static Mesh Build(Mesh src, SubmeshMapping[] mapping, int slotCount)
        {
            if (mapping.Length != src.subMeshCount) throw new ArgumentException("サブメッシュ数とマッピング数が一致しません。");
            int vc = src.vertexCount;
            var owner = new int[vc];
            for (int i = 0; i < vc; i++) owner[i] = -1;

            var transforms = new Dictionary<int, SubmeshMapping>();
            var dupSource = new List<int>();
            var dupTransform = new List<int>();
            var remap = new Dictionary<long, int>();
            var slotTriangles = new List<int>[slotCount];
            for (int i = 0; i < slotCount; i++) slotTriangles[i] = new List<int>();

            for (int s = 0; s < src.subMeshCount; s++)
            {
                var map = mapping[s];
                if (src.GetTopology(s) != MeshTopology.Triangles) throw new InvalidOperationException("三角形以外のトポロジーは未対応です。");
                if (map.TransformId != 0) transforms[map.TransformId] = map;
                var tris = src.GetTriangles(s, true);
                var target = slotTriangles[map.TargetSlot];
                foreach (var v in tris)
                {
                    int nv;
                    if (owner[v] == -1)
                    {
                        owner[v] = map.TransformId;
                        nv = v;
                    }
                    else if (owner[v] == map.TransformId)
                    {
                        nv = v;
                    }
                    else
                    {
                        long key = ((long)map.TransformId << 32) | (uint)v;
                        if (!remap.TryGetValue(key, out nv))
                        {
                            nv = vc + dupSource.Count;
                            dupSource.Add(v);
                            dupTransform.Add(map.TransformId);
                            remap[key] = nv;
                        }
                    }
                    target.Add(nv);
                }
            }

            int total = vc + dupSource.Count;
            var mesh = new Mesh { name = src.name + "_atlas" };
            mesh.indexFormat = total > 65535 ? IndexFormat.UInt32 : src.indexFormat;

            var positions = new List<Vector3>();
            src.GetVertices(positions);
            Expand(positions, dupSource);
            mesh.SetVertices(positions);

            if (src.HasVertexAttribute(VertexAttribute.Normal))
            {
                var normals = new List<Vector3>();
                src.GetNormals(normals);
                Expand(normals, dupSource);
                mesh.SetNormals(normals);
            }
            if (src.HasVertexAttribute(VertexAttribute.Tangent))
            {
                var tangents = new List<Vector4>();
                src.GetTangents(tangents);
                Expand(tangents, dupSource);
                mesh.SetTangents(tangents);
            }
            if (src.HasVertexAttribute(VertexAttribute.Color))
            {
                if (src.GetVertexAttributeFormat(VertexAttribute.Color) == VertexAttributeFormat.UNorm8)
                {
                    var colors = new List<Color32>();
                    src.GetColors(colors);
                    Expand(colors, dupSource);
                    mesh.SetColors(colors);
                }
                else
                {
                    var colors = new List<Color>();
                    src.GetColors(colors);
                    Expand(colors, dupSource);
                    mesh.SetColors(colors);
                }
            }

            int TransformOf(int v) => v < vc ? owner[v] : dupTransform[v - vc];

            for (int ch = 0; ch < 8; ch++)
            {
                var attr = VertexAttribute.TexCoord0 + ch;
                if (!src.HasVertexAttribute(attr)) continue;
                int dim = src.GetVertexAttributeDimension(attr);
                bool transformUv = ch == 0;
                if (dim <= 2)
                {
                    var uvs = new List<Vector2>();
                    src.GetUVs(ch, uvs);
                    Expand(uvs, dupSource);
                    if (transformUv)
                        for (int v = 0; v < total; v++)
                            if (transforms.TryGetValue(TransformOf(v), out var m)) uvs[v] = Apply(uvs[v], m);
                    mesh.SetUVs(ch, uvs);
                }
                else if (dim == 3)
                {
                    var uvs = new List<Vector3>();
                    src.GetUVs(ch, uvs);
                    Expand(uvs, dupSource);
                    if (transformUv)
                        for (int v = 0; v < total; v++)
                            if (transforms.TryGetValue(TransformOf(v), out var m))
                            {
                                var t = Apply(uvs[v], m);
                                uvs[v] = new Vector3(t.x, t.y, uvs[v].z);
                            }
                    mesh.SetUVs(ch, uvs);
                }
                else
                {
                    var uvs = new List<Vector4>();
                    src.GetUVs(ch, uvs);
                    Expand(uvs, dupSource);
                    if (transformUv)
                        for (int v = 0; v < total; v++)
                            if (transforms.TryGetValue(TransformOf(v), out var m))
                            {
                                var t = Apply(uvs[v], m);
                                uvs[v] = new Vector4(t.x, t.y, uvs[v].z, uvs[v].w);
                            }
                    mesh.SetUVs(ch, uvs);
                }
            }

            mesh.subMeshCount = slotCount;
            for (int i = 0; i < slotCount; i++) mesh.SetTriangles(slotTriangles[i], i, false);

            CopyBoneWeights(src, mesh, dupSource);
            CopyBlendShapes(src, mesh, dupSource);

            mesh.RecalculateBounds();
            mesh.bounds = src.bounds;
            return mesh;
        }

        static Vector2 Apply(Vector2 uv, SubmeshMapping m) => new Vector2(m.Offset.x + uv.x * m.Scale.x, m.Offset.y + uv.y * m.Scale.y);

        static void Expand<T>(List<T> list, List<int> dupSource)
        {
            foreach (var d in dupSource) list.Add(list[d]);
        }

        static void CopyBoneWeights(Mesh src, Mesh dst, List<int> dupSource)
        {
            var bonesPerVertex = src.GetBonesPerVertex();
            if (bonesPerVertex.Length == 0) return;
            var weights = src.GetAllBoneWeights();
            int vc = src.vertexCount;
            var start = new int[vc];
            int acc = 0;
            for (int i = 0; i < vc; i++)
            {
                start[i] = acc;
                acc += bonesPerVertex[i];
            }

            int total = vc + dupSource.Count;
            var counts = new byte[total];
            var outWeights = new List<BoneWeight1>(weights.Length);
            for (int v = 0; v < total; v++)
            {
                int s = v < vc ? v : dupSource[v - vc];
                counts[v] = bonesPerVertex[s];
                for (int j = 0; j < bonesPerVertex[s]; j++) outWeights.Add(weights[start[s] + j]);
            }

            var naCounts = new NativeArray<byte>(counts, Allocator.Temp);
            var naWeights = new NativeArray<BoneWeight1>(outWeights.ToArray(), Allocator.Temp);
            try
            {
                dst.SetBoneWeights(naCounts, naWeights);
            }
            finally
            {
                naCounts.Dispose();
                naWeights.Dispose();
            }
            dst.bindposes = src.bindposes;
        }

        static void CopyBlendShapes(Mesh src, Mesh dst, List<int> dupSource)
        {
            int vc = src.vertexCount;
            int total = vc + dupSource.Count;
            var dv = new Vector3[vc];
            var dn = new Vector3[vc];
            var dt = new Vector3[vc];
            for (int shape = 0; shape < src.blendShapeCount; shape++)
            {
                var name = src.GetBlendShapeName(shape);
                int frames = src.GetBlendShapeFrameCount(shape);
                for (int f = 0; f < frames; f++)
                {
                    float weight = src.GetBlendShapeFrameWeight(shape, f);
                    src.GetBlendShapeFrameVertices(shape, f, dv, dn, dt);
                    var ndv = new Vector3[total];
                    var ndn = new Vector3[total];
                    var ndt = new Vector3[total];
                    Array.Copy(dv, ndv, vc);
                    Array.Copy(dn, ndn, vc);
                    Array.Copy(dt, ndt, vc);
                    for (int i = 0; i < dupSource.Count; i++)
                    {
                        ndv[vc + i] = dv[dupSource[i]];
                        ndn[vc + i] = dn[dupSource[i]];
                        ndt[vc + i] = dt[dupSource[i]];
                    }
                    dst.AddBlendShapeFrame(name, weight, ndv, ndn, ndt);
                }
            }
        }

        /// <summary>サブメッシュが参照する頂点の UV0 範囲。UV0 が無ければ false。</summary>
        public static bool TryGetSubmeshUvBounds(Mesh mesh, int submesh, out Vector2 min, out Vector2 max)
        {
            min = new Vector2(float.MaxValue, float.MaxValue);
            max = new Vector2(float.MinValue, float.MinValue);
            if (!mesh.HasVertexAttribute(VertexAttribute.TexCoord0)) return false;
            var uvs = new List<Vector2>();
            mesh.GetUVs(0, uvs);
            if (uvs.Count != mesh.vertexCount) return false;
            foreach (var v in mesh.GetTriangles(submesh, true))
            {
                min = Vector2.Min(min, uvs[v]);
                max = Vector2.Max(max, uvs[v]);
            }
            return true;
        }
    }
}
