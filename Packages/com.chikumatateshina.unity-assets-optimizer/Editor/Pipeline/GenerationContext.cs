using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
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
        /// <summary>モバイルオーバーライドを適用するプラットフォーム（null なら Android と iPhone）。</summary>
        public string[] MobilePlatforms;
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
        /// <summary>原本テクスチャのコピー（Importer の圧縮関連設定のみ変更する）。</summary>
        public bool IsCopy;
        public Texture2D Source;
        public readonly PlannedTextureImport Import = new PlannedTextureImport();
    }

    /// <summary>Operation が意図して行った構造変更（構造検証で許容する）。</summary>
    public sealed class StructuralChange
    {
        public int RootIndex;
        public string Path;
        public string ComponentType;
        public bool Added;
        public string OperationId;
    }

    /// <summary>
    /// 生成処理のコンテキスト。Persist=false（Dry Run / NDMF）では AssetDatabase に一切書き込まない。
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
        public readonly List<StructuralChange> StructuralChanges = new List<StructuralChange>();
        readonly List<Object> _transients = new List<Object>();
        readonly Func<bool> _cancelRequested;

        /// <summary>
        /// メモリ上の生成物を最終成果物として扱う（NDMF ビルド時）。テクスチャはミップマップ生成と圧縮まで行い、破棄しない。
        /// </summary>
        public bool InMemoryFinal;
        /// <summary>InMemoryFinal 時に生成物を保存するコールバック（NDMF の AssetSaver）。</summary>
        public Action<Object> SaveTransient;

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

        public bool IsGenerated(Object o)
        {
            if (o == null) return false;
            if (!EditorUtility.IsPersistent(o)) return _transients.Contains(o);
            return Persist && AssetPathUtility.IsUnder(AssetDatabase.GetAssetPath(o), StagingFolder);
        }

        /// <summary>Mesh / Material 等を生成物として登録する。</summary>
        public T AddAsset<T>(T obj, string subFolder, string fileName, string operationId, string sourceRef) where T : Object
        {
            if (!Persist)
            {
                _transients.Add(obj);
                if (InMemoryFinal) SaveTransient?.Invoke(obj);
                return obj;
            }
            var ext = obj is Material ? ".mat" : ".asset";
            var path = AllocatePath(subFolder, fileName, ext);
            AssetDatabase.CreateAsset(obj, path);
            Outputs.Add(new OutputAsset { path = path, guid = AssetDatabase.AssetPathToGUID(path), kind = typeof(T).Name, operationId = operationId, sourceRef = sourceRef });
            return obj;
        }

        public void RegisterRemoved(int rootIndex, string path, string componentType, string operationId) =>
            StructuralChanges.Add(new StructuralChange { RootIndex = rootIndex, Path = path, ComponentType = componentType, OperationId = operationId });

        public void RegisterAdded(int rootIndex, string path, string componentType, string operationId) =>
            StructuralChanges.Add(new StructuralChange { RootIndex = rootIndex, Path = path, ComponentType = componentType, Added = true, OperationId = operationId });

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
            ApplyImport(path, record);
            record.AssetPath = path;
            record.Texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (record.Texture == null) throw new InvalidOperationException("生成テクスチャの読み込みに失敗しました: " + path);
            Outputs.Add(new OutputAsset { path = path, guid = AssetDatabase.AssetPathToGUID(path), kind = "Texture2D", operationId = record.OperationId, sourceRef = record.SourceRef });
            return record.Texture;
        }

        /// <summary>
        /// 原本テクスチャの設定変更用コピーを（まだなければ）計画する。原本の Importer は変更しない。
        /// 実体の作成と Material の差し替えは FinalizeTextures で行う。
        /// </summary>
        public GeneratedTexture GetOrPlanTextureCopy(Texture2D original, string operationId)
        {
            var existing = Textures.FirstOrDefault(t => t.IsCopy && t.Source == original);
            if (existing != null) return existing;
            var path = AssetDatabase.GetAssetPath(original);
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) return null;
            importer.GetSourceTextureWidthAndHeight(out int w, out int h);
            var record = new GeneratedTexture
            {
                IsCopy = true,
                Source = original,
                Texture = original,
                OperationId = operationId,
                SourceRef = MaterialEquivalence.StableId(original),
                Width = w,
                Height = h,
                HasAlpha = importer.DoesSourceTextureHaveAlpha(),
            };
            record.Import.NormalMap = importer.textureType == TextureImporterType.NormalMap;
            record.Import.SRgb = importer.sRGBTexture;
            record.Import.MaxSize = importer.maxTextureSize;
            record.Import.Compression = importer.textureCompression;
            record.Import.CompressionQuality = importer.compressionQuality;
            record.Import.Crunch = importer.crunchedCompression;
            record.Import.AlphaFromInput = importer.alphaSource == TextureImporterAlphaSource.FromInput;
            Textures.Add(record);
            return record;
        }

        /// <summary>
        /// 後続 Operation が変更した Importer 計画を生成テクスチャへ適用し、原本テクスチャのコピーを作成して
        /// ワーキングコピーの Material 参照を差し替える（Material が原本なら Material もコピーする）。
        /// </summary>
        public void FinalizeTextures()
        {
            if (!Persist)
            {
                if (InMemoryFinal) FinalizeInMemoryTextures();
                return;
            }
            foreach (var t in Textures.Where(t => !t.IsCopy && !string.IsNullOrEmpty(t.AssetPath)))
                ApplyImport(t.AssetPath, t);

            var copies = Textures.Where(t => t.IsCopy).ToList();
            if (copies.Count == 0) return;
            var replacement = new Dictionary<Texture, Texture>();
            foreach (var t in copies)
            {
                var srcPath = AssetDatabase.GetAssetPath(t.Source);
                var dst = AllocatePath("TextureCopies", Path.GetFileNameWithoutExtension(srcPath), Path.GetExtension(srcPath));
                if (!AssetDatabase.CopyAsset(srcPath, dst)) throw new InvalidOperationException("テクスチャのコピーに失敗しました: " + srcPath);
                ApplyImport(dst, t);
                t.AssetPath = dst;
                t.Texture = AssetDatabase.LoadAssetAtPath<Texture2D>(dst);
                replacement[t.Source] = t.Texture;
                Outputs.Add(new OutputAsset { path = dst, guid = AssetDatabase.AssetPathToGUID(dst), kind = "Texture2D(copy)", operationId = t.OperationId, sourceRef = t.SourceRef });
            }
            ReplaceTexturesInWorkingCopy(replacement, "texture-copy");
        }

        void ReplaceTexturesInWorkingCopy(Dictionary<Texture, Texture> replacement, string operationId)
        {
            var materialCopies = new Dictionary<Material, Material>();
            foreach (var pair in Copy.AllRenderers)
            {
                var mats = pair.Value.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m == null || m.shader == null || !UsesAny(m, replacement)) continue;
                    if (!materialCopies.TryGetValue(m, out var target))
                    {
                        target = IsGenerated(m) ? m : AddAsset(new Material(m) { name = m.name }, "Materials", m.name, operationId, MaterialEquivalence.StableId(m));
                        SwapTextures(target, replacement);
                        materialCopies[m] = target;
                    }
                    mats[i] = target;
                    changed = true;
                }
                if (changed) pair.Value.sharedMaterials = mats;
            }
        }

        static bool UsesAny(Material m, Dictionary<Texture, Texture> replacement)
        {
            for (int i = 0; i < m.shader.GetPropertyCount(); i++)
                if (m.shader.GetPropertyType(i) == ShaderPropertyType.Texture)
                {
                    var t = m.GetTexture(m.shader.GetPropertyName(i));
                    if (t != null && replacement.ContainsKey(t)) return true;
                }
            return false;
        }

        static void SwapTextures(Material m, Dictionary<Texture, Texture> replacement)
        {
            for (int i = 0; i < m.shader.GetPropertyCount(); i++)
            {
                if (m.shader.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                var name = m.shader.GetPropertyName(i);
                var t = m.GetTexture(name);
                if (t != null && replacement.TryGetValue(t, out var r)) m.SetTexture(name, r);
            }
        }

        /// <summary>NDMF ビルド時: 生成テクスチャをミップマップ付きで圧縮する（原本テクスチャのコピーは扱わない）。</summary>
        void FinalizeInMemoryTextures()
        {
            var target = EditorUserBuildSettings.activeBuildTarget;
            bool mobile = target == BuildTarget.Android || target == BuildTarget.iOS;
            var replacement = new Dictionary<Texture, Texture>();
            foreach (var t in Textures.Where(x => !x.IsCopy && x.Texture != null))
            {
                var src = t.Texture;
                int size = Math.Min(t.Import.MaxSize, Math.Max(src.width, src.height));
                float scale = Math.Min(1f, (float)size / Math.Max(src.width, src.height));
                int w = Math.Max(4, Mathf.RoundToInt(src.width * scale) / 4 * 4);
                int h = Math.Max(4, Mathf.RoundToInt(src.height * scale) / 4 * 4);
                var final = new Texture2D(w, h, TextureFormat.RGBA32, true, !t.Import.SRgb) { name = src.name, filterMode = t.Import.Filter, anisoLevel = t.Import.AnisoLevel, wrapMode = TextureWrapMode.Clamp };
                var px = src.GetPixels32();
                var dst = new Color32[w * h];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        dst[y * w + x] = px[Mathf.Min(src.height - 1, (int)((y + 0.5f) / h * src.height)) * src.width + Mathf.Min(src.width - 1, (int)((x + 0.5f) / w * src.width))];
                final.SetPixels32(dst);
                final.Apply(true, false);
                if (t.Import.Compression != TextureImporterCompression.Uncompressed)
                {
                    var format = mobile ? AstcTextureFormat(t.Import.MobileAstcBlock > 0 ? t.Import.MobileAstcBlock : 6)
                        : (t.HasAlpha || t.Import.NormalMap ? TextureFormat.DXT5 : TextureFormat.DXT1);
                    EditorUtility.CompressTexture(final, format, t.Import.CompressionQuality);
                }
                final.Apply(false, true);
                replacement[src] = final;
                _transients.Add(final);
                SaveTransient?.Invoke(final);
                t.Texture = final;
            }
            foreach (var pair in Copy.AllRenderers)
                foreach (var m in pair.Value.sharedMaterials)
                    if (m != null && IsGenerated(m)) SwapTextures(m, replacement);
            foreach (var src in replacement.Keys.ToList())
            {
                _transients.Remove(src);
                Object.DestroyImmediate(src);
            }
        }

        static TextureFormat AstcTextureFormat(int block)
        {
            switch (block)
            {
                case 4: return TextureFormat.ASTC_4x4;
                case 5: return TextureFormat.ASTC_5x5;
                case 8: return TextureFormat.ASTC_8x8;
                default: return TextureFormat.ASTC_6x6;
            }
        }

        void ApplyImport(string path, GeneratedTexture record)
        {
            CheckWrite(path);
            var plan = record.Import;
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) throw new InvalidOperationException("TextureImporter を取得できません: " + path);
            if (!record.IsCopy)
            {
                importer.textureType = plan.NormalMap ? TextureImporterType.NormalMap : TextureImporterType.Default;
                importer.sRGBTexture = !plan.NormalMap && plan.SRgb;
                importer.alphaSource = plan.AlphaFromInput ? TextureImporterAlphaSource.FromInput : TextureImporterAlphaSource.None;
                importer.flipGreenChannel = plan.FlipGreenChannel;
                importer.mipmapEnabled = true;
                importer.npotScale = TextureImporterNPOTScale.None;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.filterMode = plan.Filter;
                importer.anisoLevel = plan.AnisoLevel;
            }
            importer.maxTextureSize = plan.MaxSize;
            importer.textureCompression = plan.Compression;
            importer.compressionQuality = plan.CompressionQuality;
            importer.crunchedCompression = plan.Crunch;

            var platforms = plan.MobilePlatforms ?? new[] { "Android", "iPhone" };
            foreach (var platform in new[] { "Android", "iPhone" })
            {
                var ps = importer.GetPlatformTextureSettings(platform);
                bool apply = plan.MobileAstcBlock > 0 && Array.IndexOf(platforms, platform) >= 0;
                if (!apply && record.IsCopy) continue; // コピーでは原本のオーバーライドを維持する
                ps.overridden = apply;
                if (apply)
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

        /// <summary>Dry Run で生成したメモリ上のオブジェクトを破棄する（InMemoryFinal では保持する）。</summary>
        public void Dispose()
        {
            if (!InMemoryFinal)
                foreach (var o in _transients)
                    if (o != null && !EditorUtility.IsPersistent(o)) Object.DestroyImmediate(o);
            _transients.Clear();
        }
    }
}
