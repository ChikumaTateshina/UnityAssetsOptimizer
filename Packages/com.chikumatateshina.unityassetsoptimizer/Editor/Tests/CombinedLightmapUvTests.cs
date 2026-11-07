using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace ChikumaTateshina.UnityAssetsOptimizer.Tests
{
    /// <summary>結合したメッシュをライトベイクしても光が混ざらないよう、ライトマップ UV（UV1）は重ならないこと。</summary>
    public class CombinedLightmapUvTests
    {
        TestAssets _a;

        [SetUp]
        public void SetUp() => _a = new TestAssets();

        [TearDown]
        public void TearDown() => _a.Cleanup();

        GameObject Plate(string name, GameObject parent, Material mat, Vector3 position, bool withUv1)
        {
            var mesh = _a.Grid(name + "Mesh", 4);
            if (withUv1)
            {
                var uv = new List<Vector2>();
                mesh.GetUVs(0, uv);
                mesh.SetUVs(1, uv); // 各メッシュ単体では [0,1] 全体を使う（そのまま結合すると重なる）
            }
            var go = _a.MeshObject(name, parent, mesh, mat);
            go.transform.localPosition = position;
            GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.ContributeGI | StaticEditorFlags.BatchingStatic);
            return go;
        }

        Mesh CombineThree(bool withUv1)
        {
            var mat = _a.Material("M", "Standard", null);
            var root = _a.Root("Root");
            Plate("A", root, mat, new Vector3(1, 0, 1), withUv1);
            Plate("B", root, mat, new Vector3(2, 0, 1), withUv1);
            var big = Plate("C", root, mat, new Vector3(4, 0, 1), withUv1);
            big.transform.localScale = new Vector3(2, 1, 2); // 面積 4 倍
            var result = _a.Run(new[] { root }, TestAssets.Only(ContentPreset.World, FeatureIds.MeshCombine));
            Assert.IsTrue(result.Success, result.Error);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(result.GeneratedPrefabs[0]);
            var filters = prefab.GetComponentsInChildren<MeshFilter>();
            Assert.AreEqual(1, filters.Length, "three plates merged");
            return filters[0].sharedMesh;
        }

        static List<Rect> SourceRects(Mesh mesh, int sources)
        {
            var uv = new List<Vector2>();
            mesh.GetUVs(1, uv);
            int per = uv.Count / sources;
            return Enumerable.Range(0, sources).Select(s =>
            {
                var part = uv.Skip(s * per).Take(per).ToList();
                return Rect.MinMaxRect(part.Min(u => u.x), part.Min(u => u.y), part.Max(u => u.x), part.Max(u => u.y));
            }).ToList();
        }

        [Test]
        public void LightmapUvsOfCombinedSourcesDoNotOverlap()
        {
            var mesh = CombineThree(true);
            var rects = SourceRects(mesh, 3);
            for (int i = 0; i < rects.Count; i++)
            {
                Assert.IsTrue(rects[i].xMin >= 0 && rects[i].yMin >= 0 && rects[i].xMax <= 1 && rects[i].yMax <= 1, "inside 0..1: " + rects[i]);
                for (int j = i + 1; j < rects.Count; j++)
                    Assert.IsFalse(rects[i].Overlaps(rects[j]), "source " + i + " and " + j + " overlap: " + rects[i] + " / " + rects[j]);
            }
            var areas = rects.Select(r => r.width * r.height).ToList();
            Assert.Greater(areas.Max(), areas.Min() * 2f, "the larger plate gets more lightmap space");
        }

        [Test]
        public void MissingLightmapUvsAreGenerated()
        {
            var mesh = CombineThree(false);
            Assert.IsTrue(mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord1), "lightmap UVs generated for the combined mesh");
        }
    }
}
