using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace ChikumaTateshina.UnityAssetsOptimizer.Tests
{
    /// <summary>実アバターでの試験運用で見つかった問題の回帰テスト。</summary>
    public class RealWorldRegressionTests
    {
        TestAssets _a;

        [SetUp]
        public void SetUp() => _a = new TestAssets();

        [TearDown]
        public void TearDown() => _a.Cleanup();

        [Test]
        public void ColorBakeMatchesShaderMultiplication()
        {
            var white = new[] { new Color32(255, 255, 255, 255), new Color32(128, 128, 128, 128) };
            var half = new Color(0.5f, 0.5f, 0.5f, 0.5f);

            // Gamma: 値をそのまま乗算
            var gamma = TextureAtlasOperation.ColorBake.Multiply(white, half, true, false);
            Assert.AreEqual(128, gamma[0].r);
            Assert.AreEqual(64, gamma[1].r);
            Assert.AreEqual(128, gamma[0].a);

            // Linear + sRGB テクスチャ: 線形空間で乗算して sRGB に戻す（白 × 0.5(sRGB) = 0.5(sRGB)）
            var linear = TextureAtlasOperation.ColorBake.Multiply(white, half, true, true);
            Assert.AreEqual(128, linear[0].r, 1);
            Assert.Less(linear[1].r, 128, "darker than the source when multiplied");
            Assert.AreEqual(64, linear[1].a, "alpha is multiplied linearly");
        }

        [Test]
        public void ColorOnlyMaterialsAreAtlasedWithBakedColors()
        {
            var red = _a.Material("Red", "Standard", null);
            red.color = Color.red;
            var blue = _a.Material("Blue", "Standard", null);
            blue.color = Color.blue;
            var root = _a.Root("Colors");
            _a.Quad("A", root, red, Vector3.zero);
            _a.Quad("B", root, blue, Vector3.right * 2);

            var result = _a.Run(new[] { root }, TestAssets.Only(ContentPreset.World, FeatureIds.Atlas));
            Assert.IsTrue(result.Success, result.Error);
            var generated = AssetDatabase.LoadAssetAtPath<GameObject>(result.GeneratedPrefabs[0]);
            var mats = generated.GetComponentsInChildren<Renderer>().SelectMany(r => r.sharedMaterials).Distinct().ToList();
            Assert.AreEqual(1, mats.Count, "color-only materials merged");
            Assert.AreEqual(Color.white, mats[0].color, "main color moved into the texture");

            // 各 Quad の UV が指す領域の色が元の色になっている
            var tex = (Texture2D)mats[0].mainTexture;
            var path = AssetDatabase.GetAssetPath(tex);
            var bytes = System.IO.File.ReadAllBytes(System.IO.Path.Combine(UnityAssetsOptimizerInfo.ProjectRoot, path));
            var readable = new Texture2D(2, 2);
            readable.LoadImage(bytes);
            foreach (var mf in generated.GetComponentsInChildren<MeshFilter>())
            {
                var uv = mf.sharedMesh.uv;
                var center = new Vector2(uv.Average(u => u.x), uv.Average(u => u.y));
                var c = readable.GetPixelBilinear(center.x, center.y);
                var expected = mf.name == "A" ? Color.red : Color.blue;
                Assert.AreEqual(expected.r, c.r, 0.02f, mf.name + " r");
                Assert.AreEqual(expected.b, c.b, 0.02f, mf.name + " b");
            }
            Object.DestroyImmediate(readable);
        }

        [Test]
        public void HdrMainColorIsNotBaked()
        {
            var hdr = _a.Material("Hdr", "Standard", null);
            hdr.color = new Color(2, 1, 1, 1);
            var normal = _a.Material("Normal", "Standard", null);
            var root = _a.Root("Hdr");
            _a.Quad("A", root, hdr, Vector3.zero);
            _a.Quad("B", root, normal, Vector3.right * 2);
            var dry = OptimizerPipeline.DryRun(OptimizerPipeline.Analyze(new[] { root }, TestAssets.Only(ContentPreset.World, FeatureIds.Atlas)));
            var atlas = dry.Plans.First(p => p.Decision.operationId == FeatureIds.Atlas);
            Assert.IsFalse(atlas.WillExecute);
            Assert.IsTrue(atlas.Items.Any(i => i.reason.Contains("HDR")));
        }

        [Test]
        public void AnimatorParameterCurvesOnRootDoNotBlockCombine()
        {
            // ルート（パス ""）の Animator パラメータ用カーブは、子の結合・バッチングを妨げない
            var mat = _a.Material("M", "Unlit/Texture", _a.Texture("T", Color.white));
            var root = _a.Root("Animated");
            _a.Quad("A", root, mat, Vector3.zero);
            _a.Quad("B", root, mat, Vector3.right);
            var clip = new AnimationClip();
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), "SomeParameter"), AnimationCurve.Constant(0, 1, 1));
            AssetDatabase.CreateAsset(clip, TestAssets.Folder + "/Param.anim");
            var controller = AnimatorController.CreateAnimatorControllerAtPathWithClip(TestAssets.Folder + "/C.controller", clip);
            root.AddComponent<Animator>().runtimeAnimatorController = controller;

            var session = OptimizerPipeline.Analyze(new[] { root }, TestAssets.Only(ContentPreset.World, FeatureIds.MeshCombine));
            Assert.IsFalse(session.Analysis.Renderers.Any(r => r.AnimatedAny), "parameter curves are ignored");

            // 子の表示切替は結合を妨げる
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("A", typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0, 1, 1));
            session = OptimizerPipeline.Analyze(new[] { root }, TestAssets.Only(ContentPreset.World, FeatureIds.MeshCombine));
            Assert.IsTrue(session.Analysis.Renderers.First(r => r.Renderer.name == "A").AnimatedAny);
            Assert.IsFalse(session.Analysis.Renderers.First(r => r.Renderer.name == "B").AnimatedAny);
        }

        [Test]
        public void AtlasSkipsGroupsWhoseTexturesAreSharedOutside()
        {
            var texA = _a.Texture("TA", Color.red);
            var texB = _a.Texture("TB", Color.blue);
            var matA = _a.Material("MA", "Unlit/Texture", texA);
            var matB = _a.Material("MB", "Unlit/Texture", texB);
            var other = _a.Material("Other", "Unlit/Transparent", texA); // texA を統合対象外でも使う
            var root = _a.Root("Shared");
            _a.Quad("A", root, matA, Vector3.zero);
            _a.Quad("B", root, matB, Vector3.right * 2);
            _a.Quad("C", root, other, Vector3.right * 4);
            var dry = OptimizerPipeline.DryRun(OptimizerPipeline.Analyze(new[] { root }, TestAssets.Only(ContentPreset.World, FeatureIds.Atlas)));
            var atlas = dry.Plans.First(p => p.Decision.operationId == FeatureIds.Atlas);
            Assert.IsFalse(atlas.WillExecute, "duplicating a shared texture would increase texture memory");
            Assert.IsTrue(atlas.Items.Any(i => i.reason != null && i.reason.Contains("テクスチャメモリ")));
        }

        [Test]
        public void RankEvaluationDoesNotModifyOriginals()
        {
            var descriptor = System.AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor", false)).FirstOrDefault(t => t != null);
            var constraint = System.AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("VRC.SDK3.Dynamics.Constraint.Components.VRCParentConstraint", false)).FirstOrDefault(t => t != null);
            if (descriptor == null || constraint == null) Assert.Ignore("VRChat Avatars SDK が導入されていません");
            var mat = _a.Material("M", "Standard", null);
            var root = _a.Root("Avatar");
            root.AddComponent<Animator>();
            root.AddComponent(descriptor);
            var quad = _a.Quad("A", root, mat, Vector3.zero);
            quad.AddComponent(constraint);
            var settings = TestAssets.Only(ContentPreset.Avatar);
            var first = OptimizerPipeline.Analyze(new[] { root }, settings);
            var second = OptimizerPipeline.Analyze(new[] { root }, settings);
            Assert.AreEqual(RankStatus.Official, first.Ranks[0].status, string.Join(" / ", first.Ranks[0].reasons));
            Assert.AreEqual(first.InputHash, second.InputHash, "rank evaluation must not change the originals");
        }

        [Test]
        public void AtlasRespectsImporterMaxSize()
        {
            var tex = _a.Texture("Big", Color.green, 256);
            var importer = (TextureImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(tex));
            importer.maxTextureSize = 64;
            importer.SaveAndReimport();
            Assert.IsTrue(TextureReader.TryInspect(tex, AtlasChannelKind.Color, out var info, out var reason), reason);
            Assert.AreEqual(64, info.Width, "imported size, not the 256px source");
            Assert.AreEqual(64, info.Height);
        }
    }
}
