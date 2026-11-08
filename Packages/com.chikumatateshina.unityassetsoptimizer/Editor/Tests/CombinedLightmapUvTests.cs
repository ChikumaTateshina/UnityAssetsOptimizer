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

        GameObject Plate(string name, GameObject parent, Material mat, Vector3 position, bool withUv1, bool contributeGI = true)
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
            GameObjectUtility.SetStaticEditorFlags(go, contributeGI ? StaticEditorFlags.ContributeGI | StaticEditorFlags.BatchingStatic : 0);
            return go;
        }

        Mesh CombineThree(bool withUv1, bool contributeGI = true)
        {
            var mat = _a.Material("M", "Standard", null);
            var root = _a.Root("Root");
            Plate("A", root, mat, new Vector3(1, 0, 1), withUv1, contributeGI);
            Plate("B", root, mat, new Vector3(2, 0, 1), withUv1, contributeGI);
            var big = Plate("C", root, mat, new Vector3(4, 0, 1), withUv1, contributeGI);
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
        public void LightmapUvsArePreparedEvenIfNotStaticAtConversion()
        {
            // 変換後にシーンで Static にしてベイクする使い方: 変換時点では Contribute GI がない
            var mesh = CombineThree(true, contributeGI: false);
            var rects = SourceRects(mesh, 3);
            for (int i = 0; i < rects.Count; i++)
                for (int j = i + 1; j < rects.Count; j++)
                    Assert.IsFalse(rects[i].Overlaps(rects[j]), "source " + i + " and " + j + " overlap");
        }

        [Test]
        public void AtlasedMeshWithoutLightmapUvGetsOne()
        {
            // UV1 がないと UV0 でライトマップが焼かれ、アトラス化した UV0 は複数の面で領域を共有する
            var root = _a.Root("Root");
            _a.Quad("A", root, _a.Material("MA", "Standard", _a.Texture("TA", Color.red)), Vector3.zero);
            _a.Quad("B", root, _a.Material("MB", "Standard", _a.Texture("TB", Color.blue)), Vector3.right * 2);
            var result = _a.Run(new[] { root }, TestAssets.Only(ContentPreset.World, FeatureIds.Atlas));
            Assert.IsTrue(result.Success, result.Error);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(result.GeneratedPrefabs[0]);
            foreach (var f in prefab.GetComponentsInChildren<MeshFilter>())
                Assert.IsTrue(f.sharedMesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord1), f.name + " has lightmap UVs");
        }

        [Test]
        public void OverlappingSecondUvIsReplacedWithLightmapUv()
        {
            // FBX にライトマップ用ではない 2 つ目の UV（タイル状に重なる）が入っている場合
            var mesh = _a.Grid("Tiled", 8);
            var uv = new List<Vector2>();
            mesh.GetUVs(0, uv);
            mesh.SetUVs(1, uv.Select(u => new Vector2(u.x * 4 % 1f, u.y * 4 % 1f)).ToList());
            Assert.Greater(MeshMerger.LightmapUvOverlap(mesh), 0.5f, "precondition: overlapping UV1");
            var root = _a.Root("Root");
            _a.MeshObject("A", root, mesh, _a.Material("MA", "Standard", _a.Texture("TA", Color.red)));
            _a.MeshObject("B", root, _a.Grid("Plain", 2), _a.Material("MB", "Standard", _a.Texture("TB", Color.blue)));
            var result = _a.Run(new[] { root }, TestAssets.Only(ContentPreset.World, FeatureIds.Atlas));
            Assert.IsTrue(result.Success, result.Error);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(result.GeneratedPrefabs[0]);
            var converted = prefab.transform.Find("A").GetComponent<MeshFilter>().sharedMesh;
            Assert.LessOrEqual(MeshMerger.LightmapUvOverlap(converted), EmptyObjectCleaner.MaxLightmapOverlap, "replaced with a usable lightmap UV");
            Assert.AreEqual(0f, LightmapUvLayout.ChartOverlap(converted, 256), 0.001f, "no texels shared between islands");
        }

        /// <summary>島ごとに頂点を分けた四角形の集まり（各島の UV1 は uv1Rects の範囲）。</summary>
        static Mesh Islands(string name, IList<Vector2> worldSizes, IList<Rect> uv1Rects)
        {
            var mesh = new Mesh { name = name };
            var v = new List<Vector3>();
            var uv0 = new List<Vector2>();
            var uv1 = new List<Vector2>();
            var tris = new List<int>();
            float x = 0;
            for (int i = 0; i < worldSizes.Count; i++)
            {
                var s = worldSizes[i];
                var r = uv1Rects[i];
                int b = v.Count;
                v.AddRange(new[] { new Vector3(x, 0, 0), new Vector3(x + s.x, 0, 0), new Vector3(x, 0, s.y), new Vector3(x + s.x, 0, s.y) });
                uv0.AddRange(new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one });
                uv1.AddRange(new[] { r.min, new Vector2(r.xMax, r.yMin), new Vector2(r.xMin, r.yMax), r.max });
                tris.AddRange(new[] { b, b + 2, b + 1, b + 1, b + 2, b + 3 });
                x += s.x + 1;
            }
            mesh.SetVertices(v);
            mesh.SetUVs(0, uv0);
            mesh.SetUVs(1, uv1);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            return mesh;
        }

        Mesh ConvertWithAtlas(Mesh mesh)
        {
            var root = _a.Root("Root");
            _a.MeshObject("A", root, mesh, _a.Material("MA", "Standard", _a.Texture("TA", Color.red)));
            _a.MeshObject("B", root, _a.Grid("Plain", 2), _a.Material("MB", "Standard", _a.Texture("TB", Color.blue)));
            var result = _a.Run(new[] { root }, TestAssets.Only(ContentPreset.World, FeatureIds.Atlas));
            Assert.IsTrue(result.Success, result.Error);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(result.GeneratedPrefabs[0]);
            return prefab.transform.Find("A").GetComponent<MeshFilter>().sharedMesh;
        }

        [Test]
        public void UnwrappedLightmapUvIsKeptAsIs()
        {
            // 既に展開されている（島が重ならない）UV1 は再展開しない
            var mesh = Islands("Unwrapped", new[] { new Vector2(1, 1), new Vector2(2, 1) },
                new[] { new Rect(0.05f, 0.05f, 0.3f, 0.3f), new Rect(0.45f, 0.05f, 0.5f, 0.25f) });
            var original = mesh.uv2;
            var converted = ConvertWithAtlas(mesh);
            CollectionAssert.AreEqual(original, converted.uv2, "UV1 unchanged");
        }

        [Test]
        public void OverlappingIslandsAreRepackedKeepingTheirShape()
        {
            // 島どうしが重なっている（どちらも 0..1 から始まる）。展開はそのまま、島を並べ直すだけで重なりを解消する
            var mesh = Islands("Stacked", new[] { new Vector2(1, 1), new Vector2(4, 1) },
                new[] { new Rect(0, 0, 1, 1), new Rect(0, 0, 1, 0.25f) });
            Assert.Greater(LightmapUvLayout.ChartOverlap(mesh, 128), 0.1f, "precondition: overlapping islands");
            var converted = ConvertWithAtlas(mesh);
            Assert.AreEqual(8, converted.vertexCount, "not re-unwrapped (same vertices)");
            Assert.AreEqual(0f, LightmapUvLayout.ChartOverlap(converted, 256), "no overlap");
            var uv = converted.uv2;
            Vector2 Size(int b) => new Vector2((uv[b + 1] - uv[b]).magnitude, (uv[b + 2] - uv[b]).magnitude);
            var a = Size(0);
            var b2 = Size(4);
            Assert.AreEqual(1f, a.x / a.y, 0.01f, "square island stays square");
            Assert.AreEqual(4f, b2.x / b2.y, 0.04f, "4:1 island stays 4:1");
            Assert.AreEqual(1f, b2.x / a.x, 0.02f, "relative scale of the islands kept");
        }

        [Test]
        public void CombinedSourcesKeepIslandAspect()
        {
            var mat = _a.Material("M", "Standard", null);
            var root = _a.Root("Root");
            var strip = _a.MeshObject("Strip", root, Islands("Strip", new[] { new Vector2(4, 1) }, new[] { new Rect(0, 0, 1, 0.25f) }), mat);
            var square = _a.MeshObject("Square", root, Islands("Square", new[] { new Vector2(1, 1) }, new[] { new Rect(0, 0, 1, 1) }), mat);
            square.transform.localPosition = new Vector3(0, 0, 3);
            foreach (var go in new[] { strip, square }) GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.ContributeGI | StaticEditorFlags.BatchingStatic);
            var result = _a.Run(new[] { root }, TestAssets.Only(ContentPreset.World, FeatureIds.MeshCombine));
            Assert.IsTrue(result.Success, result.Error);
            var mesh = AssetDatabase.LoadAssetAtPath<GameObject>(result.GeneratedPrefabs[0]).GetComponentsInChildren<MeshFilter>().Single().sharedMesh;
            var rects = SourceRects(mesh, 2);
            float Aspect(Rect r) => Mathf.Max(r.width, r.height) / Mathf.Min(r.width, r.height);
            var aspects = rects.Select(Aspect).OrderBy(a => a).ToList(); // 結合の順序は問わない
            Assert.AreEqual(1f, aspects[0], 0.02f, "square stays square");
            Assert.AreEqual(4f, aspects[1], 0.05f, "strip keeps 4:1 (may be rotated)");
            Assert.AreEqual(0f, LightmapUvLayout.ChartOverlap(mesh, 256));
        }

        [Test]
        public void MissingLightmapUvsAreGenerated()
        {
            var mesh = CombineThree(false);
            Assert.IsTrue(mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord1), "lightmap UVs generated for the combined mesh");
        }
    }
}
