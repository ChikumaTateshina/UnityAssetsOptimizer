using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace ChikumaTateshina.AssetsOptimizer
{
    /// <summary>
    /// テクスチャの同一性キー。contentBased=true の場合はソースファイル内容と Importer 設定（.meta から GUID を除いたもの）で判定する。
    /// </summary>
    public static class TextureIdentity
    {
        public static string Key(Texture tex, bool contentBased)
        {
            if (tex == null) return "null";
            var path = AssetDatabase.GetAssetPath(tex);
            if (AssetPathUtility.IsBuiltin(path))
            {
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(tex, out string bguid, out long blocal);
                return "builtin:" + tex.name + ":" + bguid + ":" + blocal;
            }

            if (contentBased && AssetDatabase.IsMainAsset(tex))
            {
                var content = ContentKey(path);
                if (content != null) return "content:" + content;
            }

            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(tex, out string guid, out long local);
            return "obj:" + guid + ":" + local;
        }

        /// <summary>ソースファイルのハッシュ + .meta（guid 行を除く）のハッシュ。取得不能なら null。</summary>
        public static string ContentKey(string assetPath)
        {
            var full = AssetPathUtility.ToFullPath(assetPath);
            if (full == null || !File.Exists(full)) return null;
            var fileHash = HashUtility.Sha256File(full);
            var metaPath = full + ".meta";
            if (!File.Exists(metaPath)) return null;
            var meta = string.Join("\n", File.ReadAllLines(metaPath).Where(l => !l.StartsWith("guid:", StringComparison.Ordinal)));
            return fileHash + ":" + HashUtility.Sha256(meta);
        }
    }

    /// <summary>
    /// Material の等価性判定。シェーダー・描画状態・全プロパティ値が一致する場合のみ等価とする。
    /// </summary>
    public static class MaterialEquivalence
    {
        public static string Key(Material m, bool textureContentBased, bool includeNonTextureValues = true, bool includeTextures = true)
        {
            if (m == null) return "null";
            var shader = m.shader;
            if (shader == null) return "noshader:" + m.GetInstanceID();

            var sb = new StringBuilder();
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(shader, out string sguid, out long slocal);
            sb.Append("shader=").Append(shader.name).Append('|').Append(sguid).Append(':').Append(slocal).Append('\n');
            sb.Append("queue=").Append(m.renderQueue).Append('\n');
            sb.Append("keywords=").Append(string.Join(",", m.shaderKeywords.OrderBy(k => k, StringComparer.Ordinal))).Append('\n');
            sb.Append("instancing=").Append(m.enableInstancing).Append('\n');
            sb.Append("dsgi=").Append(m.doubleSidedGI).Append('\n');
            sb.Append("gi=").Append((int)m.globalIlluminationFlags).Append('\n');
            sb.Append("rendertype=").Append(m.GetTag("RenderType", false)).Append('\n');
            for (int p = 0; p < m.passCount; p++)
            {
                var pass = m.GetPassName(p);
                sb.Append("pass=").Append(pass).Append(':').Append(m.GetShaderPassEnabled(pass)).Append('\n');
            }

            int count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                var name = shader.GetPropertyName(i);
                var type = shader.GetPropertyType(i);
                if (type == ShaderPropertyType.Texture)
                {
                    if (!includeTextures) continue;
                    sb.Append(name).Append("=tex:").Append(TextureIdentity.Key(m.GetTexture(name), textureContentBased))
                      .Append(" st:").Append(F(m.GetTextureScale(name))).Append(F(m.GetTextureOffset(name))).Append('\n');
                    continue;
                }
                if (!includeNonTextureValues) continue;
                sb.Append(name).Append('=').Append(PropertyValue(m, name, type)).Append('\n');
            }
            return HashUtility.Sha256(sb.ToString());
        }

        public static string PropertyValue(Material m, string name, ShaderPropertyType type)
        {
            switch (type)
            {
                case ShaderPropertyType.Color: return F(m.GetColor(name));
                case ShaderPropertyType.Vector: return F(m.GetVector(name));
                case ShaderPropertyType.Float:
                case ShaderPropertyType.Range: return m.GetFloat(name).ToString("R", CultureInfo.InvariantCulture);
                case ShaderPropertyType.Int: return m.GetInteger(name).ToString(CultureInfo.InvariantCulture);
                default: return "?";
            }
        }

        /// <summary>等価な Material 同士を代表 Material（アセットパス順で最初）へ写像する。</summary>
        public static Dictionary<Material, Material> BuildCanonicalMap(IEnumerable<Material> materials, bool textureContentBased, out List<List<Material>> mergedGroups)
        {
            var map = new Dictionary<Material, Material>();
            mergedGroups = new List<List<Material>>();
            var groups = materials.Where(m => m != null).Distinct()
                .GroupBy(m => Key(m, textureContentBased))
                .OrderBy(g => g.Key, StringComparer.Ordinal);
            foreach (var g in groups)
            {
                var ordered = g.OrderBy(StableId, StringComparer.Ordinal).ToList();
                foreach (var m in ordered) map[m] = ordered[0];
                if (ordered.Count > 1) mergedGroups.Add(ordered);
            }
            return map;
        }

        public static string StableId(UnityEngine.Object o)
        {
            if (o == null) return string.Empty;
            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(o, out string guid, out long local))
                return AssetDatabase.GUIDToAssetPath(guid) + "#" + local.ToString("D20", CultureInfo.InvariantCulture);
            return "~instance:" + o.name;
        }

        static string F(Vector2 v) => v.x.ToString("R", CultureInfo.InvariantCulture) + "," + v.y.ToString("R", CultureInfo.InvariantCulture) + ";";
        static string F(Vector4 v) => string.Join(",", new[] { v.x, v.y, v.z, v.w }.Select(x => x.ToString("R", CultureInfo.InvariantCulture)));
        static string F(Color c) => F((Vector4)c);
    }
}
