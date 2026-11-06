using System;
using UnityEditor;

namespace ChikumaTateshina.AssetsOptimizer
{
    /// <summary>生成テクスチャのみを対象とする Importer 設定系 Operation の共通部分。</summary>
    public abstract class GeneratedTextureOperationBase : OptimizationOperationBase
    {
        protected abstract string Describe(OptimizationSettings settings);

        public override OperationAnalysis Analyze(OptimizationContext context)
        {
            var analysis = new OperationAnalysis();
            if (context.PlannedGeneratedTextureCount == 0)
                analysis.Items.Add(PlanItem.Skip("原本テクスチャ", SupportState.Unsupported,
                    "生成テクスチャがありません。原本テクスチャのコピー生成による設定変更は未実装です。"));
            else
                analysis.Items.Add(PlanItem.Supported("生成テクスチャ " + context.PlannedGeneratedTextureCount + " 枚", Describe(context.Settings)));
            analysis.Notes.Add("原本テクスチャの Importer 設定は変更しません。");
            return analysis;
        }

        public override OperationPlan Plan(OperationAnalysis analysis, OptimizationContext context, ExecutionMode mode)
        {
            var plan = CreatePlan(analysis, mode);
            plan.ExpectedEffect = Describe(context.Settings);
            return plan;
        }
    }

    public sealed class TextureDownscaleOperation : GeneratedTextureOperationBase
    {
        public override string Id => FeatureIds.TextureDownscale;

        public override OperationCapabilities Capabilities { get; } = new OperationCapabilities
        {
            Phase = 2,
            RunsAfter = new[] { FeatureIds.Atlas },
        };

        protected override string Describe(OptimizationSettings s) => "最大解像度を " + s.downscaleMaxSize + " に制限";

        public override void Execute(GenerationContext gen, OperationPlan plan)
        {
            foreach (var t in gen.Textures)
                t.Import.MaxSize = Math.Min(t.Import.MaxSize, gen.Settings.downscaleMaxSize);
        }
    }

    public sealed class TextureCompressionOperation : GeneratedTextureOperationBase
    {
        public override string Id => FeatureIds.TextureCompression;

        public override OperationCapabilities Capabilities { get; } = new OperationCapabilities
        {
            Phase = 2,
            RunsAfter = new[] { FeatureIds.Atlas, FeatureIds.TextureDownscale },
        };

        protected override string Describe(OptimizationSettings s) =>
            "PC: 圧縮（品質 " + s.compressionQuality + (s.standaloneCrunch ? ", Crunch" : string.Empty) + "）/ Android・iOS: ASTC " + s.mobileAstcBlock + "x" + s.mobileAstcBlock;

        public override void Execute(GenerationContext gen, OperationPlan plan)
        {
            foreach (var t in gen.Textures)
            {
                t.Import.Compression = TextureImporterCompression.Compressed;
                t.Import.CompressionQuality = gen.Settings.compressionQuality;
                t.Import.Crunch = gen.Settings.standaloneCrunch;
                t.Import.MobileAstcBlock = gen.Settings.mobileAstcBlock;
            }
        }
    }

    /// <summary>
    /// 未実装の機能。対応判定は常に Unsupported とし、FORCE でも実行しない（技術的に未実装の処理は不可）。
    /// </summary>
    public sealed class UnimplementedOperation : OptimizationOperationBase
    {
        readonly string _id;

        public UnimplementedOperation(string id, int phase)
        {
            _id = id;
            Capabilities = new OperationCapabilities { Implemented = false, Phase = phase };
        }

        public override string Id => _id;
        public override OperationCapabilities Capabilities { get; }

        public override OperationAnalysis Analyze(OptimizationContext context)
        {
            var analysis = new OperationAnalysis();
            analysis.Items.Add(PlanItem.Skip(DisplayName, SupportState.Unsupported, "未実装（Phase " + Capabilities.Phase + "）"));
            return analysis;
        }

        public override void Execute(GenerationContext gen, OperationPlan plan)
        {
            throw new InvalidOperationException("未実装の処理は実行できません: " + _id);
        }
    }

    public static class OperationRegistry
    {
        public static IOptimizationOperation[] CreateAll()
        {
            return new IOptimizationOperation[]
            {
                new TextureDedupeOperation(),
                new MaterialDedupeOperation(),
                new TextureAtlasOperation(),
                new TextureDownscaleOperation(),
                new TextureCompressionOperation(),
                new UnimplementedOperation(FeatureIds.MeshCombine, 2),
                new UnimplementedOperation(FeatureIds.VertexAttributeStrip, 2),
                new UnimplementedOperation(FeatureIds.MeshCompression, 2),
                new UnimplementedOperation(FeatureIds.StaticBatching, 3),
                new UnimplementedOperation(FeatureIds.GpuInstancing, 3),
                new UnimplementedOperation(FeatureIds.LightmapUv, 3),
                new UnimplementedOperation(FeatureIds.ShaderReplace, 3),
                new UnimplementedOperation(FeatureIds.PolygonReduction, 4),
                new UnimplementedOperation(FeatureIds.LodGeneration, 4),
                new UnimplementedOperation(FeatureIds.ColliderSimplify, 4),
            };
        }
    }
}
