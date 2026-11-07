using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace ChikumaTateshina.UnityAssetsOptimizer
{
    /// <summary>
    /// テクスチャの Importer 設定系 Operation の共通部分。生成テクスチャはそのまま、原本テクスチャはコピーを作って設定を変更する
    /// （原本の Importer は変更しない）。NDMF ビルド時は原本のコピーを扱わない。
    /// </summary>
    public abstract class TextureImportOperationBase : OptimizationOperationBase
    {
        protected abstract string Describe(OptimizationSettings settings);
        /// <summary>原本テクスチャに対して変更が必要か（不要なら理由）。</summary>
        protected abstract bool NeedsChange(TextureImporter importer, OptimizationSettings settings, out string reason);
        protected abstract void Apply(GeneratedTexture record, OptimizationSettings settings, TextureImporter originalImporter);

        public override OperationAnalysis Analyze(OptimizationContext context)
        {
            var analysis = new OperationAnalysis();
            if (context.PlannedGeneratedTextureCount > 0)
                analysis.Items.Add(PlanItem.Supported("生成テクスチャ " + context.PlannedGeneratedTextureCount + " 枚", Describe(context.Settings)));

            foreach (var tex in ReferencedOriginalTextures(context.Analysis.Renderers.SelectMany(r => r.Materials).Select(context.Canonical)))
            {
                var path = AssetDatabase.GetAssetPath(tex);
                var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                if (importer.textureShape != TextureImporterShape.Texture2D)
                    analysis.Items.Add(PlanItem.Skip(path, SupportState.Unsupported, "2D 以外のテクスチャ"));
                else if (!NeedsChange(importer, context.Settings, out var reason))
                    analysis.Items.Add(PlanItem.Skip(path, SupportState.Unsupported, reason));
                else
                    analysis.Items.Add(PlanItem.Supported(path, "コピーを生成して適用: " + Describe(context.Settings)));
            }
            analysis.Notes.Add("原本テクスチャの Importer 設定は変更しません（コピーを生成し、生成物の Material のみ差し替えます）。");
            return analysis;
        }

        public override OperationPlan Plan(OperationAnalysis analysis, OptimizationContext context, ExecutionMode mode)
        {
            var plan = CreatePlan(analysis, mode);
            plan.ExpectedEffect = Describe(context.Settings);
            return plan;
        }

        public static IEnumerable<Texture2D> ReferencedOriginalTextures(IEnumerable<Material> materials)
        {
            return materials.Where(m => m != null && m.shader != null).Distinct()
                .SelectMany(m => Enumerable.Range(0, m.shader.GetPropertyCount())
                    .Where(i => m.shader.GetPropertyType(i) == ShaderPropertyType.Texture)
                    .Select(i => m.GetTexture(m.shader.GetPropertyName(i))))
                .OfType<Texture2D>()
                .Where(t => EditorUtility.IsPersistent(t) && AssetDatabase.IsMainAsset(t) && AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(t)) is TextureImporter)
                .Distinct()
                .OrderBy(t => AssetDatabase.GetAssetPath(t), StringComparer.Ordinal);
        }

        public override void Execute(GenerationContext gen, OperationPlan plan)
        {
            var includedPaths = new HashSet<string>(plan.Items.Where(i => i.included).Select(i => i.target));
            foreach (var t in gen.Textures.Where(t => !t.IsCopy).ToList()) Apply(t, gen.Settings, null);
            if (gen.InMemoryFinal) return;

            var materials = gen.Copy.AllRenderers.SelectMany(p => p.Value.sharedMaterials);
            foreach (var tex in ReferencedOriginalTextures(materials).Where(t => !gen.IsGenerated(t)))
            {
                var path = AssetDatabase.GetAssetPath(tex);
                if (!includedPaths.Contains(path)) continue;
                var record = gen.GetOrPlanTextureCopy(tex, Id);
                if (record != null) Apply(record, gen.Settings, (TextureImporter)AssetImporter.GetAtPath(path));
            }
        }
    }

    public sealed class TextureDownscaleOperation : TextureImportOperationBase
    {
        public override string Id => FeatureIds.TextureDownscale;

        public override OperationCapabilities Capabilities { get; } = new OperationCapabilities
        {
            Phase = 2,
            RunsAfter = new[] { FeatureIds.Atlas, FeatureIds.MeshCombine },
        };

        protected override string Describe(OptimizationSettings s) => "最大解像度を " + s.downscaleMaxSize + " に制限";

        protected override bool NeedsChange(TextureImporter importer, OptimizationSettings s, out string reason)
        {
            importer.GetSourceTextureWidthAndHeight(out int w, out int h);
            reason = "既に " + s.downscaleMaxSize + " 以下";
            return importer.maxTextureSize > s.downscaleMaxSize && Math.Max(w, h) > s.downscaleMaxSize;
        }

        protected override void Apply(GeneratedTexture record, OptimizationSettings s, TextureImporter originalImporter)
        {
            record.Import.MaxSize = Math.Min(record.Import.MaxSize, s.downscaleMaxSize);
        }
    }

    public sealed class TextureCompressionOperation : TextureImportOperationBase
    {
        public override string Id => FeatureIds.TextureCompression;

        public override OperationCapabilities Capabilities { get; } = new OperationCapabilities
        {
            Phase = 2,
            RunsAfter = new[] { FeatureIds.Atlas, FeatureIds.TextureDownscale, FeatureIds.MeshCombine },
        };

        protected override string Describe(OptimizationSettings s) =>
            "PC: 圧縮（品質 " + s.compressionQuality + (s.standaloneCrunch ? ", Crunch" : string.Empty) + "）/ 生成テクスチャは Android・iOS: ASTC " + s.mobileAstcBlock + "x" + s.mobileAstcBlock;

        /// <summary>
        /// 原本テクスチャは非圧縮のものだけをコピー対象にする（PC 版で効果があるもの）。
        /// モバイル向けオーバーライドは Mobile Conversion（Android/iOS 派生版）で設定する。
        /// </summary>
        protected override bool NeedsChange(TextureImporter importer, OptimizationSettings s, out string reason)
        {
            reason = "既に圧縮済み（モバイル向け設定は Mobile Conversion で行います）";
            return importer.textureCompression == TextureImporterCompression.Uncompressed;
        }

        protected override void Apply(GeneratedTexture record, OptimizationSettings s, TextureImporter originalImporter)
        {
            record.Import.Compression = TextureImporterCompression.Compressed;
            record.Import.CompressionQuality = s.compressionQuality;
            record.Import.Crunch = s.standaloneCrunch;
            if (originalImporter == null)
            {
                // 生成テクスチャ（アトラス）にはモバイル向けオーバーライドも設定する
                record.Import.MobileAstcBlock = s.mobileAstcBlock;
                record.Import.MobilePlatforms = null;
            }
        }
    }

    /// <summary>
    /// 未実装の機能。対応判定は常に Unsupported とし、FORCE でも実行しない（技術的に未実装の処理は不可）。
    /// </summary>
    public sealed class UnimplementedOperation : OptimizationOperationBase
    {
        readonly string _id;
        public readonly string Reason;

        public UnimplementedOperation(string id, int phase, string reason)
        {
            _id = id;
            Reason = reason;
            Capabilities = new OperationCapabilities { Implemented = false, Phase = phase };
        }

        public override string Id => _id;
        public override OperationCapabilities Capabilities { get; }

        public override OperationAnalysis Analyze(OptimizationContext context)
        {
            var analysis = new OperationAnalysis();
            analysis.Items.Add(PlanItem.Skip(DisplayName, SupportState.Unsupported, Reason));
            return analysis;
        }

        public override void Execute(GenerationContext gen, OperationPlan plan)
        {
            throw new InvalidOperationException("未実装の処理は実行できません: " + _id);
        }
    }

    public static class OperationRegistry
    {
        public const string MeshCompressionReason =
            "実装しません: Unity のメッシュ圧縮（量子化）の仕様が公開されておらず、要件の「形状誤差を検証して不採用判定」ができないため。";
        public const string ShaderReplaceReason =
            "PC 版では行いません: シェーダー置換は Mobile Conversion（Android/iOS 派生版）で検証済みの組み合わせのみ提供します。";

        public static IOptimizationOperation[] CreateAll()
        {
            return new IOptimizationOperation[]
            {
                new TextureDedupeOperation(),
                new MaterialDedupeOperation(),
                new TextureAtlasOperation(),
                new MeshCombineOperation(),
                new PolygonReductionOperation(),
                new StaticBatchingOperation(),
                new GpuInstancingOperation(),
                new LodGenerationOperation(),
                new ColliderSimplifyOperation(),
                new LightmapUvOperation(),
                new VertexAttributeStripOperation(),
                new TextureDownscaleOperation(),
                new TextureCompressionOperation(),
                new UnimplementedOperation(FeatureIds.MeshCompression, 2, MeshCompressionReason),
                new UnimplementedOperation(FeatureIds.ShaderReplace, 3, ShaderReplaceReason),
                new BuildPolygonReductionOperation(),
                new AaoTraceAndOptimizeOperation(),
            };
        }
    }
}
