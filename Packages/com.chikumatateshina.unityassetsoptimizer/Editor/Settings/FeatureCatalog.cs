using System.Collections.Generic;

namespace ChikumaTateshina.UnityAssetsOptimizer
{
    public static class FeatureIds
    {
        public const string MaterialDedupe = "material-dedupe";
        public const string SimilarMaterialMerge = "similar-material-merge";
        public const string TextureDedupe = "texture-dedupe";
        public const string Atlas = "atlas";
        public const string TextureCompression = "texture-compression";
        public const string TextureDownscale = "texture-downscale";
        public const string MeshCombine = "mesh-combine";
        public const string VertexAttributeStrip = "vertex-attribute-strip";
        public const string MeshCompression = "mesh-compression";
        public const string PolygonReduction = "polygon-reduction";
        public const string LodGeneration = "lod-generation";
        public const string ColliderSimplify = "collider-simplify";
        public const string StaticBatching = "static-batching";
        public const string GpuInstancing = "gpu-instancing";
        public const string LightmapUv = "lightmap-uv";
        public const string ShaderReplace = "shader-replace";
        public const string AaoTraceAndOptimize = "aao-trace-and-optimize";
        public const string LightmapBake = "lightmap-bake";
        public const string BuildPolygonReduction = "build-polygon-reduction";
    }

    /// <summary>機能の既定値。null はそのプリセットでは対象外。</summary>
    public sealed class FeatureDefinition
    {
        public string Id;
        public string Label;
        public bool? WorldDefault;
        public bool? AvatarDefault;
        public int Phase;
        public string Note;

        public bool? DefaultFor(ContentPreset preset) => preset == ContentPreset.World ? WorldDefault : AvatarDefault;
    }

    /// <summary>要件定義書 FR-02 の機能表。</summary>
    public static class FeatureCatalog
    {
        public static readonly IReadOnlyList<FeatureDefinition> All = new[]
        {
            Def(FeatureIds.Atlas, "アトラス化", true, true, 1, "互換グループのみ"),
            Def(FeatureIds.MaterialDedupe, "同一Material統合", true, true, 1, "プロパティ・描画状態の等価性確認"),
            Def(FeatureIds.SimilarMaterialMerge, "近似Material統合（選択制）", true, true, 1, "似た Material を一覧表示し、選択したものだけ統合（自動では統合しない）"),
            Def(FeatureIds.TextureDedupe, "重複Texture検出/統合", true, true, 2, "内容・Importer設定を考慮（Material統合に反映）"),
            Def(FeatureIds.TextureCompression, "テクスチャ圧縮", true, true, 2, "ターゲット別設定（原本はコピーを生成して適用）"),
            Def(FeatureIds.TextureDownscale, "自動解像度削減", false, false, 2, "画質影響（原本はコピーを生成して適用）"),
            Def(FeatureIds.MeshCombine, "メッシュ結合", true, false, 2, "参照・Culling保護"),
            Def(FeatureIds.VertexAttributeStrip, "不要頂点属性削除", true, true, 2, "使用状況が不明ならスキップ"),
            Def(FeatureIds.MeshCompression, "Mesh Compression", true, true, 2, "誤差検証ができないため実行しない"),
            Def(FeatureIds.PolygonReduction, "ポリゴン削減", false, false, 4, "Phase 4"),
            Def(FeatureIds.LodGeneration, "LOD生成", false, false, 4, "Phase 4"),
            Def(FeatureIds.ColliderSimplify, "Collider簡略化", false, null, 4, "Phase 4"),
            Def(FeatureIds.StaticBatching, "Static Batching支援", true, null, 3, "競合判定"),
            Def(FeatureIds.GpuInstancing, "GPU Instancing支援", true, false, 3, "競合判定"),
            Def(FeatureIds.LightmapUv, "Lightmap UV の整備", false, null, 3, "ベイクへの影響"),
            Def(FeatureIds.LightmapBake, "ライトマップ焼き込み", false, null, 3, "Bakery / Progressive のライトマップを色のテクスチャへ直接、または光テクスチャ（UV1）として焼き込む（シーン上のオブジェクトのみ）"),
            Def(FeatureIds.ShaderReplace, "Shader自動置換", false, false, 3, "Mobile Conversion で提供"),
            Def(FeatureIds.AaoTraceAndOptimize, "AAO Trace And Optimize（ビルド時）", null, true, 3, "Avatar Optimizer（anatawa12）が必要。BlendShape・未使用オブジェクト/ボーン・PhysBone・Animator を最適化"),
            Def(FeatureIds.BuildPolygonReduction, "ビルド時ポリゴン削減（SkinnedMesh 可）", null, false, 4, "lilNDMFMeshSimplifier（lilxyzw）が必要。目標三角形数に合わせて品質を設定、顔のメッシュは保護"),
        };

        public static FeatureDefinition Get(string id)
        {
            foreach (var d in All) if (d.Id == id) return d;
            return null;
        }

        static FeatureDefinition Def(string id, string label, bool? world, bool? avatar, int phase, string note)
        {
            return new FeatureDefinition { Id = id, Label = label, WorldDefault = world, AvatarDefault = avatar, Phase = phase, Note = note };
        }
    }
}
