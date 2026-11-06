using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace ChikumaTateshina.AssetsOptimizer.Tests
{
    public class SelectionResolverTests
    {
        readonly List<GameObject> _created = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _created) if (go != null) Object.DestroyImmediate(go);
            _created.Clear();
        }

        GameObject Create(string name, GameObject parent = null)
        {
            var go = new GameObject(name);
            if (parent != null) go.transform.SetParent(parent.transform);
            else _created.Add(go);
            return go;
        }

        [Test]
        public void DescendantOfSelectedAncestorIsRemoved()
        {
            var root = Create("Root");
            var child = Create("Child", root);
            var grandChild = Create("GrandChild", child);
            var other = Create("Other");

            var resolved = SelectionResolver.Resolve(new[] { grandChild, root, other, root });

            CollectionAssert.AreEquivalent(new[] { root, other }, resolved.Roots);
            CollectionAssert.AreEquivalent(new[] { grandChild }, resolved.RemovedDescendants);
            Assert.AreEqual(2, resolved.Targets.Count);
        }
    }

    public class AtlasPackerTests
    {
        [Test]
        public void PackIsDeterministicAndNonOverlapping()
        {
            var sizes = new List<Vector2Int> { new Vector2Int(256, 256), new Vector2Int(512, 128), new Vector2Int(128, 512), new Vector2Int(64, 64) };
            var a = AtlasPacker.Pack(sizes, 4, 2048);
            var b = AtlasPacker.Pack(sizes, 4, 2048);

            Assert.AreEqual(a.Width, b.Width);
            Assert.AreEqual(a.Height, b.Height);
            CollectionAssert.AreEqual(a.Rects, b.Rects);
            Assert.AreEqual(1f, a.Scale);
            for (int i = 0; i < a.Rects.Length; i++)
            {
                var r = a.Rects[i];
                Assert.AreEqual(sizes[i], r.size);
                Assert.IsTrue(r.xMin >= 0 && r.yMin >= 0 && r.xMax <= a.Width && r.yMax <= a.Height);
                for (int j = i + 1; j < a.Rects.Length; j++) Assert.IsFalse(r.Overlaps(a.Rects[j]), "rects overlap");
            }
        }

        [Test]
        public void PackScalesDownWhenExceedingMaxSize()
        {
            var sizes = new List<Vector2Int> { new Vector2Int(1024, 1024), new Vector2Int(1024, 1024) };
            var r = AtlasPacker.Pack(sizes, 0, 1024);
            Assert.Less(r.Scale, 1f);
            Assert.LessOrEqual(r.Width, 1024);
            Assert.LessOrEqual(r.Height, 1024);
        }
    }

    public class AtlasMeshBuilderTests
    {
        static Mesh CreateTwoSubmeshMesh()
        {
            // 頂点 1 と 2 を 2 つのサブメッシュで共有する
            var mesh = new Mesh();
            mesh.SetVertices(new List<Vector3> { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(1, 1, 0) });
            mesh.SetUVs(0, new List<Vector2> { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) });
            mesh.SetUVs(1, new List<Vector2> { new Vector2(0.1f, 0.1f), new Vector2(0.2f, 0.2f), new Vector2(0.3f, 0.3f), new Vector2(0.4f, 0.4f) });
            mesh.subMeshCount = 2;
            mesh.SetTriangles(new[] { 0, 2, 1 }, 0);
            mesh.SetTriangles(new[] { 1, 2, 3 }, 1);
            mesh.AddBlendShapeFrame("shape", 100f,
                new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up }, null, null);
            return mesh;
        }

        [Test]
        public void SharedVerticesAreDuplicatedAndUvTransformed()
        {
            var src = CreateTwoSubmeshMesh();
            var mapping = new[]
            {
                new SubmeshMapping { TransformId = 1, Offset = Vector2.zero, Scale = new Vector2(0.5f, 1f), TargetSlot = 0 },
                new SubmeshMapping { TransformId = 2, Offset = new Vector2(0.5f, 0f), Scale = new Vector2(0.5f, 1f), TargetSlot = 0 },
            };
            var result = AtlasMeshBuilder.Build(src, mapping, 1);
            try
            {
                Assert.AreEqual(1, result.subMeshCount, "submeshes merged");
                Assert.AreEqual(6, result.GetIndexCount(0), "triangle count preserved");
                Assert.AreEqual(6, result.vertexCount, "two shared vertices duplicated");
                Assert.AreEqual(1, result.blendShapeCount);

                var uv0 = new List<Vector2>();
                result.GetUVs(0, uv0);
                var uv1 = new List<Vector2>();
                result.GetUVs(1, uv1);
                var tris = result.GetTriangles(0);
                // 1 つ目の三角形は左半分、2 つ目は右半分
                foreach (var v in tris.Take(3)) Assert.LessOrEqual(uv0[v].x, 0.5f + 1e-5f);
                foreach (var v in tris.Skip(3)) Assert.GreaterOrEqual(uv0[v].x, 0.5f - 1e-5f);
                // UV1（ライトマップ等）は変換しない
                Assert.AreEqual(new Vector2(0.2f, 0.2f), uv1[result.vertexCount - 2]);

                var deltas = new Vector3[result.vertexCount];
                result.GetBlendShapeFrameVertices(0, 0, deltas, null, null);
                Assert.IsTrue(deltas.All(d => d == Vector3.up), "blendshape deltas copied to duplicated vertices");
            }
            finally
            {
                Object.DestroyImmediate(src);
                Object.DestroyImmediate(result);
            }
        }
    }

    public class PlannerTests
    {
        [Test]
        public void UnimplementedOperationNeverExecutesEvenWithForce()
        {
            var settings = OptimizationSettings.CreateDefault(ContentPreset.World);
            settings.SetMode(FeatureIds.MeshCompression, ExecutionMode.Force);
            var context = new OptimizationContext { Settings = settings, Analysis = new AnalysisReport() };
            var plans = OptimizationPlanner.Plan(context, new IOptimizationOperation[] { new UnimplementedOperation(FeatureIds.MeshCompression, 2, "test") });
            Assert.IsFalse(plans[0].WillExecute);
        }

        [Test]
        public void TopologicalSortHonoursRunsAfter()
        {
            var ordered = OptimizationPlanner.TopologicalSort(OperationRegistry.CreateAll().Reverse().ToList()).Select(o => o.Id).ToList();
            Assert.Less(ordered.IndexOf(FeatureIds.TextureDedupe), ordered.IndexOf(FeatureIds.MaterialDedupe));
            Assert.Less(ordered.IndexOf(FeatureIds.MaterialDedupe), ordered.IndexOf(FeatureIds.Atlas));
            Assert.Less(ordered.IndexOf(FeatureIds.Atlas), ordered.IndexOf(FeatureIds.TextureCompression));
        }

        [Test]
        public void PresetDefaultsMatchRequirements()
        {
            var world = OptimizationSettings.CreateDefault(ContentPreset.World);
            var avatar = OptimizationSettings.CreateDefault(ContentPreset.Avatar);
            Assert.AreEqual(ExecutionMode.Auto, world.GetMode(FeatureIds.Atlas));
            Assert.AreEqual(ExecutionMode.Off, world.GetMode(FeatureIds.TextureDownscale));
            Assert.AreEqual(ExecutionMode.Off, avatar.GetMode(FeatureIds.MeshCombine));
            Assert.IsFalse(avatar.IsApplicable(FeatureIds.StaticBatching));
            Assert.IsFalse(world.IsUserModified(FeatureIds.Atlas));
            world.SetMode(FeatureIds.Atlas, ExecutionMode.Off);
            Assert.IsTrue(world.IsUserModified(FeatureIds.Atlas));
        }
    }

    public class MaterialEquivalenceTests
    {
        [Test]
        public void EqualPropertiesProduceSameKey()
        {
            var shader = Shader.Find("Unlit/Texture");
            var a = new Material(shader);
            var b = new Material(shader);
            var c = new Material(shader);
            c.mainTextureScale = new Vector2(2, 2);
            try
            {
                Assert.AreEqual(MaterialEquivalence.Key(a, false), MaterialEquivalence.Key(b, false));
                Assert.AreNotEqual(MaterialEquivalence.Key(a, false), MaterialEquivalence.Key(c, false));
            }
            finally
            {
                Object.DestroyImmediate(a);
                Object.DestroyImmediate(b);
                Object.DestroyImmediate(c);
            }
        }
    }
}
