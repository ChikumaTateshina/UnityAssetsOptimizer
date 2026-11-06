using System.Collections.Generic;

namespace ChikumaTateshina.AssetsOptimizer
{
    public static class FeatureIds
    {
        public const string MaterialDedupe = "material-dedupe";
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
            Def(FeatureIds.TextureDedupe, "重複Texture検出/統合", true, true, 2, "内容・Importer設定を考慮"),
            Def(FeatureIds.TextureCompression, "テクスチャ圧縮", true, true, 2, "ターゲット別設定（生成テクスチャのみ）"),
            Def(FeatureIds.TextureDownscale, "自動解像度削減", false, false, 2, "画質影響（生成テクスチャのみ）"),
            Def(FeatureIds.MeshCombine, "メッシュ結合", true, false, 2, "参照・Culling保護"),
            Def(FeatureIds.VertexAttributeStrip, "不要頂点属性削除", true, true, 2, "使用状況が不明ならスキップ"),
            Def(FeatureIds.MeshCompression, "Mesh Compression", true, true, 2, "形状誤差を検証"),
            Def(FeatureIds.PolygonReduction, "ポリゴン削減", false, false, 4, "Phase 4"),
            Def(FeatureIds.LodGeneration, "LOD生成", false, false, 4, "Phase 4"),
            Def(FeatureIds.ColliderSimplify, "Collider簡略化", false, null, 4, "Phase 4"),
            Def(FeatureIds.StaticBatching, "Static Batching支援", true, null, 3, "競合判定"),
            Def(FeatureIds.GpuInstancing, "GPU Instancing支援", true, false, 3, "競合判定"),
            Def(FeatureIds.LightmapUv, "Lightmap UV再生成", false, null, 3, "ベイクへの影響"),
            Def(FeatureIds.ShaderReplace, "Shader自動置換", false, false, 3, "専用アダプターのみ"),
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
