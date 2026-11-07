using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ChikumaTateshina.UnityAssetsOptimizer.Tests
{
    /// <summary>発光・色の焼き込み、ないテクスチャの補完、オブジェクトの大きさに応じた配分。</summary>
    public class AtlasBakeTests
    {
        TestAssets _a;

        [SetUp]
        public void SetUp() => _a = new TestAssets();

        [TearDown]
        public void TearDown() => _a.Cleanup();

        static OptimizationSettings Settings() => TestAssets.Only(ContentPreset.World, FeatureIds.Atlas);

        Material Standard(string name, Color texColor)
        {
            var m = _a.Material(name, "Standard", _a.Texture("T" + name, texColor));
            EditorUtility.SetDirty(m);
            return m;
        }

        static void Emission(Material m, Color color)
        {
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", color);
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        }

        (GameObject root, RunResult result, Material atlas, GameObject prefab) Run(Material a, Material b, Vector3? scaleB = null)
        {
            var root = _a.Root("Root");
            _a.Quad("A", root, a, Vector3.zero);
            var qb = _a.Quad("B", root, b, Vector3.right * 2);
            if (scaleB.HasValue) qb.transform.localScale = scaleB.Value;
            var result = _a.Run(new[] { root }, Settings());
            Assert.IsTrue(result.Success, result.Error);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(result.GeneratedPrefabs[0]);
            var mats = prefab.GetComponentsInChildren<Renderer>().Select(r => r.sharedMaterial).Distinct().ToList();
            Assert.AreEqual(1, mats.Count, "merged into one atlas material: " + string.Join(", ", result.Report.operations.SelectMany(o => o.items).Select(i => i.reason)));
            return (root, result, mats[0], prefab);
        }

        /// <summary>保存された画像を直接デコードし、Renderer の UV の中心（領域の中心）の色を返す。</summary>
        static Color32 RegionCenter(Texture texture, GameObject prefab, string child)
        {
            var mesh = prefab.transform.Find(child).GetComponent<MeshFilter>().sharedMesh;
            var uv = mesh.uv;
            var center = new Vector2(uv.Average(u => u.x), uv.Average(u => u.y));
            var decoded = new Texture2D(2, 2);
            try
            {
                decoded.LoadImage(File.ReadAllBytes(Path.Combine(UnityAssetsOptimizerInfo.ProjectRoot, AssetDatabase.GetAssetPath(texture))));
                return decoded.GetPixel((int)(center.x * decoded.width), (int)(center.y * decoded.height));
            }
            finally
            {
                Object.DestroyImmediate(decoded);
            }
        }

        static void Near(Color32 expected, Color32 actual, int tolerance = 6)
        {
            Assert.IsTrue(Mathf.Abs(expected.r - actual.r) <= tolerance && Mathf.Abs(expected.g - actual.g) <= tolerance && Mathf.Abs(expected.b - actual.b) <= tolerance,
                "expected " + expected + " but was " + actual);
        }

        [Test]
        public void EmissionIsBakedAndDisabledEmissionBecomesBlack()
        {
            var a = Standard("A", Color.white);
            Emission(a, new Color(2f, 0f, 0f, 1f)); // HDR
            var b = Standard("B", Color.white);   // 発光なし
            var (_, _, atlas, prefab) = Run(a, b);

            Assert.IsTrue(atlas.IsKeywordEnabled("_EMISSION"));
            var color = atlas.GetColor("_EmissionColor");
            Assert.AreEqual(2f, color.r, 0.01f, "HDR strength moves to the material");
            Assert.AreEqual(color.r, color.g, 0.001f, "white × strength");
            var map = atlas.GetTexture("_EmissionMap");
            Assert.IsNotNull(map);
            Near(new Color32(255, 0, 0, 255), RegionCenter(map, prefab, "A"));
            Near(new Color32(0, 0, 0, 255), RegionCenter(map, prefab, "B"));
            Assert.AreEqual(MaterialGlobalIlluminationFlags.RealtimeEmissive, atlas.globalIlluminationFlags);
        }

        [Test]
        public void SolidEmissionMaskUsesSmallerTexture()
        {
            var a = Standard("A", Color.white);
            Emission(a, Color.green);
            var b = Standard("B", Color.gray);
            var (_, _, atlas, _) = Run(a, b);
            var main = atlas.GetTexture("_MainTex");
            var emission = atlas.GetTexture("_EmissionMap");
            Assert.Less(emission.width, main.width, "emission mask with only solid colors needs less resolution");
        }

        [Test]
        public void MissingNormalMapIsFilledFlat()
        {
            var a = Standard("A", Color.white);
            var normal = _a.Texture("N", new Color(0.6f, 0.4f, 1f));
            var importer = (TextureImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(normal));
            importer.textureType = TextureImporterType.NormalMap;
            importer.SaveAndReimport();
            a.SetTexture("_BumpMap", normal);
            a.EnableKeyword("_NORMALMAP");
            var b = Standard("B", Color.white); // ノーマルマップなし
            var (_, result, atlas, prefab) = Run(a, b);

            Assert.IsTrue(atlas.IsKeywordEnabled("_NORMALMAP"));
            var bump = atlas.GetTexture("_BumpMap");
            Assert.IsNotNull(bump);
            Near(new Color32(128, 128, 255, 255), RegionCenter(bump, prefab, "B"));
            StringAssert.Contains("_BumpMap", result.Report.operations.First(o => o.decision.operationId == FeatureIds.Atlas).expectedEffect);
        }

        [Test]
        public void MaterialColorIsBakedIntoTexture()
        {
            var a = Standard("A", Color.white);
            a.color = new Color(1f, 0.5f, 0f);
            var b = Standard("B", Color.white);
            var (_, _, atlas, prefab) = Run(a, b);
            Assert.AreEqual(Color.white, atlas.color);
            Near(new Color32(255, 128, 0, 255), RegionCenter(atlas.mainTexture, prefab, "A"));
        }

        [Test]
        public void MetallicAndSmoothnessAreBaked()
        {
            var a = Standard("A", Color.white);
            a.SetFloat("_Glossiness", 0f);
            var b = Standard("B", Color.white);
            b.SetFloat("_Glossiness", 0.75f);
            b.SetFloat("_Metallic", 1f);
            var (_, _, atlas, prefab) = Run(a, b);

            Assert.IsTrue(atlas.IsKeywordEnabled("_METALLICGLOSSMAP"));
            Assert.AreEqual(1f, atlas.GetFloat("_GlossMapScale"));
            var map = atlas.GetTexture("_MetallicGlossMap");
            Assert.IsNotNull(map);
            var pa = RegionCenter(map, prefab, "A");
            var pb = RegionCenter(map, prefab, "B");
            Assert.AreEqual(0, pa.r, 2);
            Assert.AreEqual(0, pa.a, 2);
            Assert.AreEqual(255, pb.r, 2, "metallic 1");
            Assert.AreEqual(191, pb.a, 2, "smoothness 0.75");
        }

        [Test]
        public void OpaqueStandardAtlasDropsAlpha()
        {
            var a = Standard("A", new Color(1f, 0f, 0f, 0.5f)); // アルファのある画像でも Opaque なら使われない
            var b = Standard("B", Color.blue);
            var (_, _, atlas, _) = Run(a, b);
            Assert.AreEqual(".jpg", Path.GetExtension(AssetDatabase.GetAssetPath(atlas.mainTexture)), "saved without alpha");
        }

        [Test]
        public void SmallObjectsGetFewerTexels()
        {
            var a = Standard("A", Color.red);
            var b = Standard("B", Color.blue);
            var (_, result, _, prefab) = Run(a, b, new Vector3(0.1f, 0.1f, 1f)); // B は面積が 1/100
            float UvWidth(string child)
            {
                var uv = prefab.transform.Find(child).GetComponent<MeshFilter>().sharedMesh.uv;
                return uv.Max(u => u.x) - uv.Min(u => u.x);
            }
            Assert.Greater(UvWidth("A"), UvWidth("B") * 1.5f, "the larger object gets a larger region");
        }
    }
}
