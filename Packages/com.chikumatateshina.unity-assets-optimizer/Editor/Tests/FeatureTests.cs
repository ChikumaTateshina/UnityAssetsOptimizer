using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace ChikumaTateshina.AssetsOptimizer.Tests
{
    public class FeatureTests
    {
        TestAssets _a;
        Func<long> _freeSpace;

        [SetUp]
        public void SetUp()
        {
            _a = new TestAssets();
            _freeSpace = OptimizerPipeline.FreeSpaceProvider;
        }

        [TearDown]
        public void TearDown()
        {
            OptimizerPipeline.FreeSpaceProvider = _freeSpace;
            _a.Cleanup();
        }

        GameObject TwoQuads(out string[] originals)
        {
            var texA = _a.Texture("TexA", Color.red);
            var texB = _a.Texture("TexB", Color.blue);
            var matA = _a.Material("MatA", "Unlit/Texture", texA);
            var matB = _a.Material("MatB", "Unlit/Texture", texB);
            var root = _a.Root("Root");
            _a.Quad("QuadA", root, matA, Vector3.zero);
            _a.Quad("QuadB", root, matB, Vector3.right * 2);
            originals = new[] { TestAssets.Folder + "/TexA.png", TestAssets.Folder + "/TexB.png", TestAssets.Folder + "/MatA.mat", TestAssets.Folder + "/MatB.mat" };
            return root;
        }

        [Test]
        public void DrawCallsAreModelEstimatesNotMeasurements()
        {
            var root = TwoQuads(out _);
            var session = OptimizerPipeline.Analyze(new[] { root }, TestAssets.Only(ContentPreset.World));
            var draws = session.Baseline.Get(MetricKeys.DrawCalls);
            Assert.AreEqual(MetricKind.Estimated, draws.kind);
            Assert.AreEqual(2, draws.value);
            Assert.AreEqual(MetricKind.Unavailable, session.Baseline.Get(MetricKeys.GpuTime).kind);
        }

        [Test]
        public void PrefabAssetIsOutputAsVariant()
        {
            var root = TwoQuads(out _);
            var prefabPath = TestAssets.Folder + "/Source.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            var result = _a.Run(new[] { prefab }, TestAssets.Only(ContentPreset.World, FeatureIds.Atlas));
            Assert.IsTrue(result.Success, result.Error);
            var generated = AssetDatabase.LoadAssetAtPath<GameObject>(result.GeneratedPrefabs[0]);
            Assert.AreEqual(PrefabAssetType.Variant, PrefabUtility.GetPrefabAssetType(generated));
            Assert.AreEqual(prefab, PrefabUtility.GetCorrespondingObjectFromSource(generated));
            Assert.AreEqual(1, generated.GetComponentsInChildren<Renderer>().SelectMany(r => r.sharedMaterials).Distinct().Count());
        }

        [Test]
        public void StaticMeshesAreCombinedAndOriginalsUntouched()
        {
            var root = TwoQuads(out var originals);
            var before = _a.Hash(originals);
            var result = _a.Run(new[] { root }, TestAssets.Only(ContentPreset.World, FeatureIds.Atlas, FeatureIds.MeshCombine));
            Assert.IsTrue(result.Success, result.Error);
            var generated = AssetDatabase.LoadAssetAtPath<GameObject>(result.GeneratedPrefabs[0]);
            var renderers = generated.GetComponentsInChildren<MeshRenderer>(true);
            Assert.AreEqual(1, renderers.Length, "two quads merged into one renderer");
            Assert.AreEqual(4, result.After.Get(MetricKeys.Triangles).value);
            Assert.AreEqual(1, result.After.Get(MetricKeys.DrawCalls).value);
            Assert.AreEqual(2, generated.GetComponentsInChildren<Transform>(true).Length - 1, "GameObjects are kept");
            CollectionAssert.AreEqual(before, _a.Hash(originals));
            Assert.AreEqual(2, root.GetComponentsInChildren<MeshRenderer>().Length, "original scene objects unchanged");
        }

        [Test]
        public void DecimatorReducesFlatGridWithinTolerance()
        {
            var mesh = _a.Grid("Grid", 16);
            var result = MeshDecimator.Decimate(mesh, 0.5f, 0.001f);
            try
            {
                Assert.LessOrEqual(result.ResultTriangles, result.SourceTriangles / 2 + 1);
                Assert.LessOrEqual(result.MaxError, result.ErrorLimit);
                Assert.AreEqual(mesh.bounds.size, result.Mesh.bounds.size);
                var uv = new List<Vector2>();
                result.Mesh.GetUVs(0, uv);
                var pos = new List<Vector3>();
                result.Mesh.GetVertices(pos);
                for (int i = 0; i < pos.Count; i++) Assert.AreEqual(new Vector2(pos[i].x, pos[i].z), uv[i], "UV of kept vertices is preserved");
            }
            finally
            {
                Object.DestroyImmediate(result.Mesh);
            }
        }

        [Test]
        public void LodGroupIsGeneratedAndValidated()
        {
            var mat = _a.Material("Mat", "Unlit/Texture", _a.Texture("Tex", Color.green));
            var root = _a.Root("LodRoot");
            _a.MeshObject("Plane", root, _a.Grid("Grid", 16), mat);
            var settings = TestAssets.Only(ContentPreset.World, FeatureIds.LodGeneration);
            settings.decimationMaxError = 0.001f;
            var result = _a.Run(new[] { root }, settings);
            Assert.IsTrue(result.Success, result.Error);
            var generated = AssetDatabase.LoadAssetAtPath<GameObject>(result.GeneratedPrefabs[0]);
            var group = generated.GetComponentInChildren<LODGroup>();
            Assert.IsNotNull(group);
            Assert.GreaterOrEqual(group.GetLODs().Length, 2);
        }

        [Test]
        public void UnusedVertexAttributesAreStrippedForUnlit()
        {
            var root = TwoQuads(out _);
            var result = _a.Run(new[] { root }, TestAssets.Only(ContentPreset.Avatar, FeatureIds.VertexAttributeStrip));
            Assert.IsTrue(result.Success, result.Error);
            var generated = AssetDatabase.LoadAssetAtPath<GameObject>(result.GeneratedPrefabs[0]);
            foreach (var mf in generated.GetComponentsInChildren<MeshFilter>())
            {
                Assert.IsFalse(mf.sharedMesh.HasVertexAttribute(VertexAttribute.Normal), "Unlit/Texture does not use normals");
                Assert.IsTrue(mf.sharedMesh.HasVertexAttribute(VertexAttribute.TexCoord0));
            }
        }

        [Test]
        public void OriginalTexturesAreCompressedViaCopies()
        {
            var root = TwoQuads(out var originals);
            foreach (var path in originals.Where(p => p.EndsWith(".png")))
            {
                var imp = (TextureImporter)AssetImporter.GetAtPath(path);
                imp.textureCompression = TextureImporterCompression.Uncompressed;
                imp.SaveAndReimport();
            }
            var before = _a.Hash(originals);
            var result = _a.Run(new[] { root }, TestAssets.Only(ContentPreset.World, FeatureIds.TextureCompression));
            Assert.IsTrue(result.Success, result.Error);
            var generated = AssetDatabase.LoadAssetAtPath<GameObject>(result.GeneratedPrefabs[0]);
            foreach (var r in generated.GetComponentsInChildren<Renderer>())
            {
                var tex = r.sharedMaterial.mainTexture;
                var path = AssetDatabase.GetAssetPath(tex);
                Assert.IsTrue(AssetPathUtility.IsUnder(path, result.RunFolder), "material uses the texture copy");
                var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                Assert.AreEqual(TextureImporterCompression.Compressed, importer.textureCompression);
            }
            CollectionAssert.AreEqual(before, _a.Hash(originals), "original importer settings unchanged");
        }

        [Test]
        public void StaticBatchingFlagIsSetOnGeneratedCopyOnly()
        {
            var root = TwoQuads(out _);
            var result = _a.Run(new[] { root }, TestAssets.Only(ContentPreset.World, FeatureIds.StaticBatching));
            Assert.IsTrue(result.Success, result.Error);
            var generated = AssetDatabase.LoadAssetAtPath<GameObject>(result.GeneratedPrefabs[0]);
            foreach (var r in generated.GetComponentsInChildren<MeshRenderer>())
                Assert.IsTrue((GameObjectUtility.GetStaticEditorFlags(r.gameObject) & StaticEditorFlags.BatchingStatic) != 0);
            foreach (var r in root.GetComponentsInChildren<MeshRenderer>())
                Assert.AreEqual((StaticEditorFlags)0, GameObjectUtility.GetStaticEditorFlags(r.gameObject));
        }

        [Test]
        public void ExternalReferenceBlocksExecution()
        {
            var root = TwoQuads(out _);
            var outside = _a.Root("Outside");
            root.GetComponentInChildren<MeshRenderer>().probeAnchor = outside.transform;
            var session = OptimizerPipeline.Analyze(new[] { root }, TestAssets.Only(ContentPreset.World, FeatureIds.Atlas));
            Assert.IsTrue(session.Analysis.HasBlockingIssues);
            var dry = OptimizerPipeline.DryRun(session);
            Assert.IsFalse(dry.CanExecute);
        }

        [Test]
        public void InsufficientDiskSpaceAborts()
        {
            var root = TwoQuads(out _);
            OptimizerPipeline.FreeSpaceProvider = () => 0;
            var result = _a.Run(new[] { root }, TestAssets.Only(ContentPreset.World, FeatureIds.Atlas));
            Assert.IsFalse(result.Success);
            StringAssert.Contains("ディスク", result.Error);
        }

        [Test]
        public void CorruptedManifestIsReportedAndNotRestored()
        {
            var folder = VaoInfo.RunFolder("test-corrupt");
            _a.RunFolders.Add(folder);
            AssetPathUtility.EnsureFolder(folder);
            File.WriteAllText(Path.Combine(VaoInfo.ProjectRoot, folder, VaoInfo.ManifestFileName), "{ broken");
            var run = RestoreService.FindRuns().First(r => r.RunFolder == folder);
            Assert.IsNotNull(run.Error);
        }

        [Test]
        public void DiscardRefusesFoldersOutsideManagedRoot()
        {
            Assert.IsFalse(RestoreService.DiscardRun(TestAssets.Folder, out var error));
            StringAssert.Contains("管理領域外", error);
            Assert.IsTrue(AssetDatabase.IsValidFolder(TestAssets.Folder));
        }

        [Test]
        public void VisualDiffDetectsAppearanceChanges()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                var none = VisualDiff.Compare(new[] { TwoQuads(out _) }, new GameObject[0]);
                Assert.IsFalse(none.available, "no graphics device → unavailable, never fabricated");
                Assert.Ignore("グラフィックスデバイスがないため描画比較は省略");
            }
            var root = TwoQuads(out _);
            var same = VisualDiff.Compare(new[] { root }, new[] { root });
            Assert.IsTrue(same.available, same.reason);
            Assert.Less(same.meanAbsDiff, 0.001);

            var other = _a.Root("Other");
            var white = _a.Material("W", "Unlit/Texture", _a.Texture("TW", Color.white));
            _a.Quad("QuadA", other, white, Vector3.zero);
            _a.Quad("QuadB", other, white, Vector3.right * 2);
            var changed = VisualDiff.Compare(new[] { root }, new[] { other });
            Assert.Greater(changed.changedPixelRatio, 0.01);
        }

        [Test]
        public void StructuralValidatorRejectsUnregisteredChanges()
        {
            var a = _a.Root("A");
            var b = _a.Root("B");
            new GameObject("Child").transform.SetParent(a.transform);
            new GameObject("Child").transform.SetParent(b.transform);
            b.transform.GetChild(0).gameObject.AddComponent<BoxCollider>();
            Assert.IsFalse(StructuralValidator.Compare(a, b).Passed);
            var allowed = new[] { new StructuralChange { RootIndex = 0, Path = "Child[0]", ComponentType = typeof(BoxCollider).FullName, Added = true } };
            Assert.IsTrue(StructuralValidator.Compare(a, b, allowed).Passed);
        }
    }

    /// <summary>VRChat SDK / lilToon / NDMF が導入されたプロジェクトでのみ実行される連携テスト。</summary>
    public class IntegrationWithVrcPackagesTests
    {
        TestAssets _a;

        [SetUp]
        public void SetUp() => _a = new TestAssets();

        [TearDown]
        public void TearDown() => _a.Cleanup();

        static Type FindType(string name) =>
            AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name, false)).FirstOrDefault(t => t != null);

        GameObject Avatar(Material matA, Material matB)
        {
            var descriptorType = FindType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor");
            if (descriptorType == null) Assert.Ignore("VRChat Avatars SDK が導入されていません");
            var root = _a.Root("Avatar");
            root.AddComponent<Animator>();
            root.AddComponent(descriptorType);
            _a.Quad("A", root, matA, Vector3.zero);
            _a.Quad("B", root, matB, Vector3.right);
            return root;
        }

        [Test]
        public void SdkOfficialRankIsUsed()
        {
            var root = Avatar(_a.Material("A", "Standard", null), _a.Material("B", "Standard", null));
            var ranks = OptimizerPipeline.EvaluateRanks(new[] { root }, false);
            Assert.AreEqual(3, ranks.Count);
            foreach (var r in ranks)
            {
                Assert.AreEqual(RankStatus.Official, r.status, string.Join(" / ", r.reasons));
                Assert.IsNotEmpty(r.rows);
                Assert.AreNotEqual("算出不可", r.rank);
                var poly = r.rows.FirstOrDefault(x => x.key == "PolyCount");
                Assert.IsNotNull(poly, "SDK のカテゴリ PolyCount");
                Assert.AreEqual(4, poly.value, "2 quads = 4 triangles");
                Assert.AreNotEqual("—", poly.threshold, "thresholds come from the SDK level set");
            }
        }

        [Test]
        public void AndroidConversionIsVerifiedBySdk()
        {
            var tex = _a.Texture("T", Color.white);
            var root = Avatar(_a.Material("A", "Standard", tex), _a.Material("B", "Standard", tex));
            if (!CapabilityDatabase.IsConversionEnabled(VrcPlatform.Android, ContentPreset.Avatar, out var reason)) Assert.Ignore(reason);
            var result = _a.Run(new[] { root }, TestAssets.Only(ContentPreset.Avatar), RunKind.Android);
            Assert.IsTrue(result.Success, result.Error);
            Assert.AreEqual(ConversionState.Verified, result.Conversion.state, string.Join(" / ", result.Conversion.reasons));
            var generated = AssetDatabase.LoadAssetAtPath<GameObject>(result.GeneratedPrefabs[0]);
            Assert.IsTrue(generated.GetComponentsInChildren<Renderer>().All(r => r.sharedMaterial.shader.name == "VRChat/Mobile/Standard Lite"));
            Assert.IsTrue(root.GetComponentsInChildren<Renderer>().All(r => r.sharedMaterial.shader.name == "Standard"), "PC original unchanged");
        }

        [Test]
        public void NonEquivalentConversionRequiresForce()
        {
            var tex = _a.Texture("T", Color.white);
            var root = Avatar(_a.Material("A", "Unlit/Texture", tex), _a.Material("B", "Unlit/Texture", tex));
            if (!CapabilityDatabase.IsConversionEnabled(VrcPlatform.Android, ContentPreset.Avatar, out var reason)) Assert.Ignore(reason);
            var session = OptimizerPipeline.Analyze(new[] { root }, TestAssets.Only(ContentPreset.Avatar), RunKind.Android);
            var dry = OptimizerPipeline.DryRun(session);
            var plan = dry.Plans.First(p => p.Decision.operationId == MobileShaderConversionOperation.OperationId);
            Assert.IsFalse(plan.WillExecute, "Unlit → Toon Lit is not equivalent; AUTO must skip it");
            Assert.AreNotEqual(ConversionState.Verified, dry.Conversion.state);
        }

        [Test]
        public void LilToonMaterialsAreAtlased()
        {
            if (Shader.Find("lilToon") == null) Assert.Ignore("lilToon が導入されていません");
            var matA = _a.Material("LA", "lilToon", _a.Texture("TA", Color.red));
            var matB = _a.Material("LB", "lilToon", _a.Texture("TB", Color.blue));
            var root = _a.Root("Lil");
            _a.Quad("A", root, matA, Vector3.zero);
            _a.Quad("B", root, matB, Vector3.right);
            var result = _a.Run(new[] { root }, TestAssets.Only(ContentPreset.Avatar, FeatureIds.Atlas));
            Assert.IsTrue(result.Success, result.Error);
            var generated = AssetDatabase.LoadAssetAtPath<GameObject>(result.GeneratedPrefabs[0]);
            var mats = generated.GetComponentsInChildren<Renderer>().SelectMany(r => r.sharedMaterials).Distinct().ToList();
            Assert.AreEqual(1, mats.Count);
            Assert.AreEqual("lilToon", mats[0].shader.name);
        }

        [Test]
        public void LilToonUvAnimationIsRejected()
        {
            if (Shader.Find("lilToon") == null) Assert.Ignore("lilToon が導入されていません");
            var mat = _a.Material("LA", "lilToon", _a.Texture("TA", Color.red));
            mat.SetVector("_MainTex_ScrollRotate", new Vector4(1, 0, 0, 0));
            Assert.IsNotNull(ShaderAdapterRegistry.CheckPreconditions(ShaderAdapterRegistry.Find(mat.shader), mat));
        }

        const string PoiyomiShader = ".poiyomi/Poiyomi Toon";

        [Test]
        public void PoiyomiMaterialsAreAtlased()
        {
            if (Shader.Find(PoiyomiShader) == null) Assert.Ignore("Poiyomi Toon が導入されていません");
            var matA = _a.Material("PA", PoiyomiShader, _a.Texture("TA", Color.red));
            var matB = _a.Material("PB", PoiyomiShader, _a.Texture("TB", Color.blue));
            var root = _a.Root("Poi");
            _a.Quad("A", root, matA, Vector3.zero);
            _a.Quad("B", root, matB, Vector3.right);
            var settings = TestAssets.Only(ContentPreset.Avatar, FeatureIds.Atlas);
            var dry = OptimizerPipeline.DryRun(OptimizerPipeline.Analyze(new[] { root }, settings));
            var atlas = dry.Plans.First(p => p.Decision.operationId == FeatureIds.Atlas);
            Assert.IsTrue(atlas.WillExecute, string.Join(" / ", atlas.Items.Select(i => i.target + ": " + i.reason)) + " " + dry.Error);
            var result = _a.Run(new[] { root }, settings);
            Assert.IsTrue(result.Success, result.Error);
            var generated = AssetDatabase.LoadAssetAtPath<GameObject>(result.GeneratedPrefabs[0]);
            var mats = generated.GetComponentsInChildren<Renderer>().SelectMany(r => r.sharedMaterials).Distinct().ToList();
            Assert.AreEqual(1, mats.Count);
            Assert.AreEqual(PoiyomiShader, mats[0].shader.name);
        }

        [Test]
        public void PoiyomiUvSettingsAreRejected()
        {
            if (Shader.Find(PoiyomiShader) == null) Assert.Ignore("Poiyomi Toon が導入されていません");
            var adapter = ShaderAdapterRegistry.Find(Shader.Find(PoiyomiShader));
            Assert.IsNotNull(adapter);
            var mat = _a.Material("PA", PoiyomiShader, _a.Texture("TA", Color.red));
            Assert.IsNull(ShaderAdapterRegistry.CheckPreconditions(adapter, mat), "default material is accepted");

            mat.SetVector("_UVSettingsPan0", new Vector4(0.1f, 0, 0, 0));
            Assert.IsNotNull(ShaderAdapterRegistry.CheckPreconditions(adapter, mat), "global UV0 pan");
            mat.SetVector("_UVSettingsPan0", Vector4.zero);

            var main = adapter.Channels.First(c => c.Property == "_MainTex");
            mat.SetFloat("_MainTexUV", 1);
            Assert.IsTrue(main.Rules.Any(r => r.Check(mat) != null), "_MainTex on UV1");
        }

        [Test]
        public void PoiyomiAndroidConversionNeedsForce()
        {
            if (Shader.Find(PoiyomiShader) == null) Assert.Ignore("Poiyomi Toon が導入されていません");
            var tex = _a.Texture("T", Color.white);
            var root = Avatar(_a.Material("PA", PoiyomiShader, tex), _a.Material("PB", PoiyomiShader, tex));
            if (!CapabilityDatabase.IsConversionEnabled(VrcPlatform.Android, ContentPreset.Avatar, out var reason)) Assert.Ignore(reason);
            var dry = OptimizerPipeline.DryRun(OptimizerPipeline.Analyze(new[] { root }, TestAssets.Only(ContentPreset.Avatar), RunKind.Android));
            var plan = dry.Plans.First(p => p.Decision.operationId == MobileShaderConversionOperation.OperationId);
            Assert.IsFalse(plan.WillExecute);
            Assert.IsTrue(plan.Items.All(i => i.support == SupportState.Unknown && i.forceable), "non-equivalent → FORCE only");
        }

        [Test]
        public void NdmfBuildAppliesOptimizationToBuildCloneOnly()
        {
            var processor = FindType("nadena.dev.ndmf.AvatarProcessor");
            var component = FindType("ChikumaTateshina.AssetsOptimizer.Ndmf.VaoBuildOptimizer");
            if (processor == null || component == null) Assert.Ignore("NDMF が導入されていません");
            var root = Avatar(_a.Material("A", "Unlit/Texture", _a.Texture("TA", Color.red)), _a.Material("B", "Unlit/Texture", _a.Texture("TB", Color.blue)));
            root.AddComponent(component);
            var clone = Object.Instantiate(root);
            _a.SceneObjects.Add(clone);
            processor.GetMethod("ProcessAvatar", new[] { typeof(GameObject) }).Invoke(null, new object[] { clone });

            Assert.AreEqual(1, clone.GetComponentsInChildren<Renderer>().SelectMany(r => r.sharedMaterials).Distinct().Count(), "atlas applied at build");
            Assert.IsNull(clone.GetComponent(component), "marker component removed");
            Assert.AreEqual(2, root.GetComponentsInChildren<Renderer>().SelectMany(r => r.sharedMaterials).Distinct().Count(), "original untouched");
        }
    }
}
