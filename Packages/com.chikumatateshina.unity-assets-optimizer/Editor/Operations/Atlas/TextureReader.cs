using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ChikumaTateshina.AssetsOptimizer
{
    public sealed class TextureSourceInfo
    {
        public Texture2D Texture;
        public string AssetPath;
        public TextureImporter Importer;
        /// <summary>true: ソースファイル（PNG/JPEG）を直接デコード。false: インポート済みテクスチャを GPU 経由で読み戻し（非可逆）。</summary>
        public bool FromSourceFile;
        public int Width;
        public int Height;
        public bool SRgb;
        public bool IsNormal;
        public bool FlipGreen;
        public bool AlphaFromInput;
        public FilterMode Filter;
        public int AnisoLevel;

        /// <summary>同一アトラスチャンネルにまとめるために一致が必要な Importer 設定。</summary>
        public string ImportKey => string.Join("|", IsNormal, SRgb, FlipGreen, AlphaFromInput);
    }

    /// <summary>
    /// テクスチャを読み取り専用で取得する。原本の Importer 設定（Read/Write 等）は変更しない。
    /// </summary>
    public static class TextureReader
    {
        static readonly string[] DirectExtensions = { ".png", ".jpg", ".jpeg" };
        static readonly string[] BlitExtensions = { ".tga", ".psd", ".tif", ".tiff", ".bmp", ".gif" };

        public static bool TryInspect(Texture tex, AtlasChannelKind kind, out TextureSourceInfo info, out string reason)
        {
            info = null;
            reason = null;
            if (!(tex is Texture2D tex2D))
            {
                reason = "Texture2D 以外のテクスチャ";
                return false;
            }
            var path = AssetDatabase.GetAssetPath(tex);
            if (AssetPathUtility.IsBuiltin(path) || !AssetDatabase.IsMainAsset(tex))
            {
                reason = "組み込み/サブアセットのテクスチャ";
                return false;
            }
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null)
            {
                reason = "TextureImporter 以外でインポートされたテクスチャ";
                return false;
            }
            if (importer.textureShape != TextureImporterShape.Texture2D)
            {
                reason = "2D 以外のテクスチャ形状";
                return false;
            }
            if (importer.alphaSource == TextureImporterAlphaSource.FromGrayScale)
            {
                reason = "Alpha Source = From Gray Scale は未対応";
                return false;
            }

            var ext = Path.GetExtension(path).ToLowerInvariant();
            bool direct = Array.IndexOf(DirectExtensions, ext) >= 0;
            bool blit = Array.IndexOf(BlitExtensions, ext) >= 0;

            if (kind == AtlasChannelKind.Normal)
            {
                if (importer.textureType != TextureImporterType.NormalMap || importer.convertToNormalmap)
                {
                    reason = "ノーマルマップ設定でないテクスチャ（またはグレースケールから生成）";
                    return false;
                }
                if (!direct)
                {
                    reason = "ノーマルマップは PNG/JPEG ソースのみ対応（圧縮後データの読み戻しは符号化が不明）";
                    return false;
                }
            }
            else
            {
                if (importer.textureType != TextureImporterType.Default)
                {
                    reason = "Texture Type が Default 以外（" + importer.textureType + "）";
                    return false;
                }
                if (!direct && !blit)
                {
                    reason = "未対応のソース形式（" + ext + "、HDR 等）";
                    return false;
                }
            }

            info = new TextureSourceInfo
            {
                Texture = tex2D,
                AssetPath = path,
                Importer = importer,
                FromSourceFile = direct,
                SRgb = kind != AtlasChannelKind.Normal && importer.sRGBTexture,
                IsNormal = kind == AtlasChannelKind.Normal,
                FlipGreen = kind == AtlasChannelKind.Normal && importer.flipGreenChannel,
                AlphaFromInput = importer.alphaSource == TextureImporterAlphaSource.FromInput,
                Filter = tex2D.filterMode,
                AnisoLevel = tex2D.anisoLevel,
            };

            if (direct)
            {
                importer.GetSourceTextureWidthAndHeight(out int w, out int h);
                info.Width = w;
                info.Height = h;
            }
            else
            {
                info.Width = tex2D.width;
                info.Height = tex2D.height;
            }
            if (info.Width <= 0 || info.Height <= 0)
            {
                reason = "テクスチャサイズを取得できません";
                info = null;
                return false;
            }
            return true;
        }

        public static Color32[] ReadPixels(TextureSourceInfo info, out int width, out int height)
        {
            if (info.FromSourceFile)
            {
                var full = AssetPathUtility.ToFullPath(info.AssetPath);
                var bytes = File.ReadAllBytes(full);
                var tmp = new Texture2D(2, 2, TextureFormat.RGBA32, false, !info.SRgb);
                try
                {
                    if (!tmp.LoadImage(bytes, false)) throw new InvalidOperationException("画像のデコードに失敗しました: " + info.AssetPath);
                    width = tmp.width;
                    height = tmp.height;
                    return tmp.GetPixels32();
                }
                finally
                {
                    Object.DestroyImmediate(tmp);
                }
            }

            width = info.Width;
            height = info.Height;
            var rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32,
                info.SRgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
            var previous = RenderTexture.active;
            var readback = new Texture2D(width, height, TextureFormat.RGBA32, false, !info.SRgb);
            try
            {
                Graphics.Blit(info.Texture, rt);
                RenderTexture.active = rt;
                readback.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                readback.Apply();
                return readback.GetPixels32();
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
                Object.DestroyImmediate(readback);
            }
        }
    }
}
