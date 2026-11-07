using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ChikumaTateshina.UnityAssetsOptimizer.Tests
{
    public class AtlasFileFormatTests
    {
        TestAssets _a;

        [SetUp]
        public void SetUp() => _a = new TestAssets();

        [TearDown]
        public void TearDown() => _a.Cleanup();

        string AtlasPath(OptimizationSettings settings, string shader, Color a, Color b)
        {
            var matA = _a.Material("MA", shader, _a.Texture("TA", a));
            var matB = _a.Material("MB", shader, _a.Texture("TB", b));
            var root = _a.Root("Root");
            _a.Quad("A", root, matA, Vector3.zero);
            _a.Quad("B", root, matB, Vector3.right * 2);
            var result = _a.Run(new[] { root }, settings);
            Assert.IsTrue(result.Success, result.Error);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(result.GeneratedPrefabs[0]);
            return AssetDatabase.GetAssetPath(prefab.GetComponentInChildren<Renderer>().sharedMaterial.mainTexture);
        }

        [Test]
        public void OpaqueAtlasIsSavedAsJpeg()
        {
            var path = AtlasPath(TestAssets.Only(ContentPreset.World, FeatureIds.Atlas), "Unlit/Texture", Color.red, Color.blue);
            Assert.AreEqual(".jpg", Path.GetExtension(path));
        }

        [Test]
        public void AtlasWithAlphaStaysPng()
        {
            var path = AtlasPath(TestAssets.Only(ContentPreset.World, FeatureIds.Atlas), "Unlit/Transparent", new Color(1, 0, 0, 0.5f), Color.blue);
            Assert.AreEqual(".png", Path.GetExtension(path), "JPEG cannot store alpha");
        }

        [Test]
        public void PngSettingKeepsPng()
        {
            var settings = TestAssets.Only(ContentPreset.World, FeatureIds.Atlas);
            settings.atlasImageFormat = AtlasImageFormat.Png;
            var path = AtlasPath(settings, "Unlit/Texture", Color.red, Color.blue);
            Assert.AreEqual(".png", Path.GetExtension(path));
        }

        [Test]
        public void JpegQuality95IsVisuallyLossless()
        {
            // 写真に近い（グラデーション＋細かな模様）画像で、品質 95 の劣化量を PSNR で確認する
            const int size = 256;
            var src = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var px = new Color32[size * size];
            var rng = new System.Random(1);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int n = rng.Next(-12, 13);
                    px[y * size + x] = new Color32(
                        (byte)Mathf.Clamp(x + n, 0, 255),
                        (byte)Mathf.Clamp(y + n, 0, 255),
                        (byte)Mathf.Clamp(128 + (int)(60 * Math.Sin(x * 0.2) * Math.Cos(y * 0.15)) + n, 0, 255), 255);
                }
            src.SetPixels32(px);
            src.Apply();
            var decoded = new Texture2D(2, 2);
            try
            {
                decoded.LoadImage(src.EncodeToJPG(95));
                var back = decoded.GetPixels32();
                double mse = 0;
                for (int i = 0; i < px.Length; i++)
                    mse += (Sq(px[i].r - back[i].r) + Sq(px[i].g - back[i].g) + Sq(px[i].b - back[i].b)) / 3.0;
                mse /= px.Length;
                double psnr = 10 * Math.Log10(255.0 * 255.0 / Math.Max(mse, 1e-9));
                Assert.GreaterOrEqual(psnr, 40.0, "PSNR " + psnr.ToString("0.0") + " dB");
            }
            finally
            {
                Object.DestroyImmediate(src);
                Object.DestroyImmediate(decoded);
            }
        }

        static double Sq(int v) => v * v;
    }
}
