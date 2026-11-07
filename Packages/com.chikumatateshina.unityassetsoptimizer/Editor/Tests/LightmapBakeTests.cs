using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ChikumaTateshina.UnityAssetsOptimizer.Tests
{
    /// <summary>
    /// ライトマップ焼き込み。ベイクには時間がかかるため、HDR のライトマップテクスチャをシーンのライトマップとして設定し、
    /// Renderer にライトマップの番号と位置を割り当てて検証する（左半分 = 0.5、右半分 = 2.0 の光）。
    /// </summary>
    public class LightmapBakeTests
    {
        TestAssets _a;
        LightmapData[] _previous;
        Texture2D _lightmap;

        [SetUp]
        public void SetUp()
        {
            _a = new TestAssets();
            _previous = LightmapSettings.lightmaps;
            const int size = 64;
            _lightmap = new Texture2D(size, size, TextureFormat.RGBAHalf, false, true) { name = "TestLightmap" };
            var px = new Color[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    px[y * size + x] = x < size / 2 ? new Color(0.5f, 0.5f, 0.5f, 1) : new Color(2f, 2f, 2f, 1);
            _lightmap.SetPixels(px);
            _lightmap.Apply(false, false);
            LightmapSettings.lightmaps = new[] { new LightmapData { lightmapColor = _lightmap } };
        }

        [TearDown]
        public void TearDown()
        {
            LightmapSettings.lightmaps = _previous;
            Object.DestroyImmediate(_lightmap);
            _a.Cleanup();
        }

        GameObject Scene()
        {
            var mat = _a.Material("M", "Standard", _a.Texture("T", Color.white));
            var root = _a.Root("Room");
            var dark = _a.Quad("Dark", root, mat, Vector3.zero).GetComponent<MeshRenderer>();
            var bright = _a.Quad("Bright", root, mat, Vector3.right * 2).GetComponent<MeshRenderer>();
            foreach (var r in new[] { dark, bright })
            {
                GameObjectUtility.SetStaticEditorFlags(r.gameObject, StaticEditorFlags.ContributeGI | StaticEditorFlags.BatchingStatic);
                r.lightmapIndex = 0;
            }
            dark.lightmapScaleOffset = new Vector4(0.5f, 1f, 0f, 0f);
            bright.lightmapScaleOffset = new Vector4(0.5f, 1f, 0.5f, 0f);
            return root;
        }

        static OptimizationSettings Settings(LightBakeShaderMode mode, float strength = 1f)
        {
            var s = TestAssets.Only(ContentPreset.World, FeatureIds.LightmapBake);
            s.lightBakeShaderMode = mode;
            s.lightBakeStrength = strength;
            return s;
        }

        /// <summary>Renderer の UV1 の中心で、光テクスチャ（保存された画像）の値を読む。</summary>
        static Color32 LightAt(GameObject prefab, string child, Texture lightTexture)
        {
            var uv1 = prefab.transform.Find(child).GetComponent<MeshFilter>().sharedMesh.uv2;
            var center = new Vector2(uv1.Average(u => u.x), uv1.Average(u => u.y));
            var decoded = new Texture2D(2, 2);
            try
            {
                decoded.LoadImage(File.ReadAllBytes(Path.Combine(UnityAssetsOptimizerInfo.ProjectRoot, AssetDatabase.GetAssetPath(lightTexture))));
                return decoded.GetPixel((int)(center.x * decoded.width), (int)(center.y * decoded.height));
            }
            finally
            {
                Object.DestroyImmediate(decoded);
            }
        }

        static byte Neutral(bool linear) => (byte)Mathf.RoundToInt((linear ? Mathf.LinearToGammaSpace(1f / 4.594794f) : 0.5f) * 255);

        [Test]
        public void UnlitModeBakesEachObjectsOwnLight()
        {
            var root = Scene();
            var result = _a.Run(new[] { root }, Settings(LightBakeShaderMode.Unlit));
            Assert.IsTrue(result.Success, result.Error);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(result.GeneratedPrefabs[0]);
            var dark = prefab.transform.Find("Dark").GetComponent<Renderer>();
            var mat = dark.sharedMaterial;
            Assert.AreEqual(LightmapBakeOperation.UnlitShaderName, mat.shader.name);
            Assert.AreEqual(1f, mat.GetFloat("_LightStrength"));
            Assert.IsNotNull(mat.GetTexture("_MainTex"), "albedo is kept, not duplicated");
            var light = mat.GetTexture("_LightTex");
            Assert.IsNotNull(light);

            bool linear = PlayerSettings.colorSpace == ColorSpace.Linear;
            var neutral = Neutral(linear);
            var d = LightAt(prefab, "Dark", light);
            var b = LightAt(prefab, "Bright", light);
            Assert.Less(d.r, neutral - 10, "the dark side gets less light (shared material, own light)");
            Assert.Greater(b.r, neutral + 10, "the bright side gets more light");
            Assert.AreEqual(0, (int)(GameObjectUtility.GetStaticEditorFlags(dark.gameObject) & StaticEditorFlags.ContributeGI), "excluded from future bakes");
            Assert.AreEqual(Neutral(linear) > 0, true);
            Assert.AreEqual("Standard", root.transform.Find("Dark").GetComponent<Renderer>().sharedMaterial.shader.name, "original unchanged");
        }

        [Test]
        public void KeepShaderModeUsesStandardDetail()
        {
            var root = Scene();
            var result = _a.Run(new[] { root }, Settings(LightBakeShaderMode.KeepShader));
            Assert.IsTrue(result.Success, result.Error);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(result.GeneratedPrefabs[0]);
            var mat = prefab.transform.Find("Bright").GetComponent<Renderer>().sharedMaterial;
            Assert.AreEqual("Standard", mat.shader.name);
            Assert.IsTrue(mat.IsKeywordEnabled("_DETAIL_MULX2"));
            Assert.AreEqual(1f, mat.GetFloat("_UVSec"));
            Assert.IsNotNull(mat.GetTexture("_DetailAlbedoMap"));
        }

        [Test]
        public void ZeroStrengthIsNeutral()
        {
            var root = Scene();
            var result = _a.Run(new[] { root }, Settings(LightBakeShaderMode.KeepShader, 0f));
            Assert.IsTrue(result.Success, result.Error);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(result.GeneratedPrefabs[0]);
            var light = prefab.transform.Find("Dark").GetComponent<Renderer>().sharedMaterial.GetTexture("_DetailAlbedoMap");
            var neutral = Neutral(PlayerSettings.colorSpace == ColorSpace.Linear);
            Assert.AreEqual(neutral, LightAt(prefab, "Dark", light).r, 3);
            Assert.AreEqual(neutral, LightAt(prefab, "Bright", light).r, 3);
        }

        [Test]
        public void PrefabAssetsAreSkipped()
        {
            var prefab = PrefabUtility.SaveAsPrefabAsset(Scene(), TestAssets.Folder + "/Room.prefab");
            var dry = OptimizerPipeline.DryRun(OptimizerPipeline.Analyze(new[] { prefab }, Settings(LightBakeShaderMode.Unlit)));
            var plan = dry.Plans.First(p => p.Decision.operationId == FeatureIds.LightmapBake);
            Assert.IsFalse(plan.WillExecute);
            Assert.IsTrue(plan.Items.All(i => i.reason.Contains("Prefab アセット")), string.Join(" / ", plan.Items.Select(i => i.reason)));
        }
    }
}
