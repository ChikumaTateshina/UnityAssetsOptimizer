using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ChikumaTateshina.AssetsOptimizer
{
    public enum AtlasChannelKind { Color, Data, Normal }

    public sealed class AtlasChannel
    {
        public string Property;
        public AtlasChannelKind Kind;

        public AtlasChannel(string property, AtlasChannelKind kind)
        {
            Property = property;
            Kind = kind;
        }
    }

    public interface IShaderAdapter
    {
        string Id { get; }
        int Version { get; }
        bool Matches(Shader shader);
        /// <summary>アトラス化可能なテクスチャプロパティ。UV0 + 単位 ST のみを前提とする。</summary>
        IReadOnlyList<AtlasChannel> Channels { get; }
        /// <summary>非 null の場合はアトラス化できないテクスチャプロパティ（Detail map 等、別 UV/別 ST を使うもの）。</summary>
        IReadOnlyList<string> ForbiddenTextures { get; }
    }

    /// <summary>シェーダー名で一致を判定する単純なアダプター。</summary>
    public sealed class NamedShaderAdapter : IShaderAdapter
    {
        readonly string _shaderName;

        public string Id { get; }
        public int Version { get; }
        public IReadOnlyList<AtlasChannel> Channels { get; }
        public IReadOnlyList<string> ForbiddenTextures { get; }

        public NamedShaderAdapter(string id, int version, string shaderName, AtlasChannel[] channels, string[] forbidden = null)
        {
            Id = id;
            Version = version;
            _shaderName = shaderName;
            Channels = channels;
            ForbiddenTextures = forbidden ?? new string[0];
        }

        public bool Matches(Shader shader) => shader != null && shader.name == _shaderName;
    }

    /// <summary>
    /// 対応シェーダーの登録。ここに無いシェーダー（lilToon, Poiyomi, VRChat/Mobile/* 等）は
    /// プロパティ仕様を検証するまでアトラス化しない（Documentation~/CompatibilityMatrix.md 参照）。
    /// </summary>
    public static class ShaderAdapterRegistry
    {
        static readonly List<IShaderAdapter> Adapters = new List<IShaderAdapter>
        {
            new NamedShaderAdapter("unity.standard", 1, "Standard", new[]
            {
                new AtlasChannel("_MainTex", AtlasChannelKind.Color),
                new AtlasChannel("_MetallicGlossMap", AtlasChannelKind.Data),
                new AtlasChannel("_BumpMap", AtlasChannelKind.Normal),
                new AtlasChannel("_ParallaxMap", AtlasChannelKind.Data),
                new AtlasChannel("_OcclusionMap", AtlasChannelKind.Data),
                new AtlasChannel("_EmissionMap", AtlasChannelKind.Color),
                new AtlasChannel("_DetailMask", AtlasChannelKind.Data),
            }, new[] { "_DetailAlbedoMap", "_DetailNormalMap" }),
            new NamedShaderAdapter("unity.standard-specular", 1, "Standard (Specular setup)", new[]
            {
                new AtlasChannel("_MainTex", AtlasChannelKind.Color),
                new AtlasChannel("_SpecGlossMap", AtlasChannelKind.Data),
                new AtlasChannel("_BumpMap", AtlasChannelKind.Normal),
                new AtlasChannel("_ParallaxMap", AtlasChannelKind.Data),
                new AtlasChannel("_OcclusionMap", AtlasChannelKind.Data),
                new AtlasChannel("_EmissionMap", AtlasChannelKind.Color),
                new AtlasChannel("_DetailMask", AtlasChannelKind.Data),
            }, new[] { "_DetailAlbedoMap", "_DetailNormalMap" }),
            MainTexOnly("unity.unlit-texture", "Unlit/Texture"),
            MainTexOnly("unity.unlit-transparent", "Unlit/Transparent"),
            MainTexOnly("unity.unlit-transparent-cutout", "Unlit/Transparent Cutout"),
            MainTexOnly("unity.legacy-diffuse", "Legacy Shaders/Diffuse"),
            MainTexOnly("unity.mobile-diffuse", "Mobile/Diffuse"),
        };

        public static IReadOnlyList<IShaderAdapter> All => Adapters;

        public static IShaderAdapter Find(Shader shader) => Adapters.FirstOrDefault(a => a.Matches(shader));

        static IShaderAdapter MainTexOnly(string id, string shaderName) =>
            new NamedShaderAdapter(id, 1, shaderName, new[] { new AtlasChannel("_MainTex", AtlasChannelKind.Color) });
    }
}
