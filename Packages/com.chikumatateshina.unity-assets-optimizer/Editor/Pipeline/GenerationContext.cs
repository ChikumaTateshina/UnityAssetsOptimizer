using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ChikumaTateshina.AssetsOptimizer
{
    /// <summary>生成テクスチャに最終的に適用する Importer 設定。Dry Run の容量推定にも使う。</summary>
    public sealed class PlannedTextureImport
    {
        public bool NormalMap;
        public bool SRgb = true;
        public bool AlphaFromInput = true;
        public bool FlipGreenChannel;
        public FilterMode Filter = FilterMode.Bilinear;
        public int AnisoLevel = 1;
        public int MaxSize = 2048;
        public TextureImporterCompression Compression = TextureImporterCompression.Compressed;
        public int CompressionQuality = 50;
        public bool Crunch;
        /// <summary>モバイル向け ASTC ブロックサイズ（0 ならオーバーライドなし）。</summary>
        public int MobileAstcBlock;
    }

    public sealed class GeneratedTexture
    {
        public Texture2D Texture;
        public string AssetPath;
        public string OperationId;
        public string SourceRef;
        public int Width;
        public int Height;
        public bool HasAlpha;
        public readonly PlannedTextureImport Import = new PlannedTextureImport();
    }

    /// <summary>
    /// 生成処理のコンテキスト。Persist=false（Dry Run）では AssetDatabase に一切書き込まない。
    /// Persist=true でもステージングフォルダ以外への書き込みは WriteGuard で拒否する。
    /// </summary>
    public sealed class GenerationContext : IDisposable
    {
        public readonly bool Persist;
        public readonly string StagingFolder;
        public readonly OptimizationSettings Settings;
        public readonly WorkingCopy Copy;
        public readonly RunLog Log;
        public readonly List<OutputAsset> Outputs = new List<OutputAsset>();
        public readonly List<GeneratedTexture> Textures = new List<GeneratedTexture>();
        readonly List<Object> _transients = new List<Object>();
        readonly Func<bool> _cancelRequested;

        public GenerationContext(bool persist, string stagingFolder, OptimizationSettings settings, WorkingCopy copy, RunLog log, Func<bool> cancelRequested)
        {
            Persist = persist;
            StagingFolder = stagingFolder;
            Settings = settings;
            Copy = copy;
            Log = log;
            _cancelRequested = cancelRequested;
        }

        public void ThrowIfCancelled()
        {
            if (_cancelRequested != null && _cancelRequested()) throw new OperationCanceledException("ユーザーによりキャンセルされました。");
        }

        public void CheckWrite(string assetPath)
        {
            if (!Persist || string.IsNullOrEmpty(StagingFolder) || !AssetPathUtility.IsUnder(assetPath, StagingFolder))
                throw new InvalidOperationException("書き込み先がステージング領域外です（拒否）: " + assetPath);
        }

        string AllocatePath(string subFolder, string fileName, string extension)
        {
            var folder = StagingFolder + "/" + subFolder;
            CheckWrite(folder);
            AssetPathUtility.EnsureFolder(folder);
            var path = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + AssetPathUtility.SanitizeFileName(fileName) + extension);
            CheckWrite(path);
            return path;
        }

        /// <summary>Mesh / Material 等を生成物として登録する。</summary>
        public T AddAsset<T>(T obj, string subFolder, string fileName, string operationId, string sourceRef) where T : Object
        {
            if (!Persist)
            {
                _transients.Add(obj);
                return obj;
            }
            var ext = obj is Material ? ".mat" : ".asset";
            var path = AllocatePath(subFolder, fileName, ext);
            AssetDatabase.CreateAsset(obj, path);
            Outputs.Add(new OutputAsset { path = path, guid = AssetDatabase.AssetPathToGUID(path), kind = typeof(T).Name, operationId = operationId, sourceRef = sourceRef });
            return obj;
        }

        /// <summary>メモリ上のテクスチャを PNG として生成物に登録する。Importer 設定は FinalizeTextures でまとめて適用する。</summary>
        public Texture2D AddTexture(Texture2D inMemory, GeneratedTexture record, string fileName)
        {
            record.Width = inMemory.width;
            record.Height = inMemory.height;
            Textures.Add(record);
            if (!Persist)
            {
                _transients.Add(inMemory);
                record.Texture = inMemory;
                return inMemory;
            }

            var path = AllocatePath("Textures", fileName, ".png");
            File.WriteAllBytes(Path.Combine(VaoInfo.ProjectRoot, path), inMemory.EncodeToPNG());
            Object.DestroyImmediate(inMemory);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            ApplyImport(path, record.Import);
            record.AssetPath = path;
            record.Texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (record.Texture == null) throw new InvalidOperationException("生成テクスチャの読み込みに失敗しました: " + path);
            Outputs.Add(new OutputAsset { path = path, guid = AssetDatabase.AssetPathToGUID(path), kind = "Texture2D", operationId = record.OperationId, sourceRef = record.SourceRef });
            return record.Texture;
        }

        /// <summary>後続 Operation が変更した Importer 計画を生成テクスチャへ適用する。</summary>
        public void FinalizeTextures()
        {
            if (!Persist) return;
            foreach (var t in Textures)
            {
                if (string.IsNullOrEmpty(t.AssetPath)) continue;
                ApplyImport(t.AssetPath, t.Import);
            }
        }

        void ApplyImport(string path, PlannedTextureImport plan)
        {
            CheckWrite(path);
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) throw new InvalidOperationException("TextureImporter を取得できません: " + path);
            importer.textureType = plan.NormalMap ? TextureImporterType.NormalMap : TextureImporterType.Default;
            importer.sRGBTexture = !plan.NormalMap && plan.SRgb;
            importer.alphaSource = plan.AlphaFromInput ? TextureImporterAlphaSource.FromInput : TextureImporterAlphaSource.None;
            importer.flipGreenChannel = plan.FlipGreenChannel;
            importer.mipmapEnabled = true;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = plan.Filter;
            importer.anisoLevel = plan.AnisoLevel;
            importer.maxTextureSize = plan.MaxSize;
            importer.textureCompression = plan.Compression;
            importer.compressionQuality = plan.CompressionQuality;
            importer.crunchedCompression = plan.Crunch;

            foreach (var platform in new[] { "Android", "iPhone" })
            {
                var ps = importer.GetPlatformTextureSettings(platform);
                ps.overridden = plan.MobileAstcBlock > 0;
                if (ps.overridden)
                {
                    ps.maxTextureSize = plan.MaxSize;
                    ps.format = AstcFormat(plan.MobileAstcBlock);
                    ps.compressionQuality = plan.CompressionQuality;
                }
                importer.SetPlatformTextureSettings(ps);
            }
            importer.SaveAndReimport();
        }

        public static TextureImporterFormat AstcFormat(int block)
        {
            switch (block)
            {
                case 4: return TextureImporterFormat.ASTC_4x4;
                case 5: return TextureImporterFormat.ASTC_5x5;
                case 8: return TextureImporterFormat.ASTC_8x8;
                default: return TextureImporterFormat.ASTC_6x6;
            }
        }

        /// <summary>Dry Run で生成したメモリ上のオブジェクトを破棄する。</summary>
        public void Dispose()
        {
            foreach (var o in _transients)
                if (o != null && !EditorUtility.IsPersistent(o)) Object.DestroyImmediate(o);
            _transients.Clear();
        }
    }
}
