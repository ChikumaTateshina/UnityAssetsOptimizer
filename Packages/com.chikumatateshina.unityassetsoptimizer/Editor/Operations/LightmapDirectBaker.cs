using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ChikumaTateshina.UnityAssetsOptimizer
{
    /// <summary>
    /// ライトマップを色のテクスチャへ直接焼き込む。アトラスの領域は複数のオブジェクト（場所ごとに光が違う）で共有され、
    /// メッシュの中でも UV0 が重なる（タイル等）ことがあるため、各オブジェクトの重ならない UV（ライトマップ UV）の配置で
    /// 新しいテクスチャを作り、各画素に「色（UV0 で補間）× 光（ライトマップ UV で補間）」を描く。
    /// 解像度は元の色テクスチャのテクセル密度（ワールド面積あたりの画素数）に合わせる。
    /// 結果のメッシュは UV0 を新しい配置にし、Material は Unlit（光は色に入っている）にする。発光は同じ配置の別テクスチャに焼く。
    /// </summary>
    public static class LightmapDirectBaker
    {
        const int Padding = 4;
        const int DilatePasses = 4;

        public sealed class Target
        {
            public RendererInfo Info;
            public int LightmapIndex;
            public Vector4 ScaleOffset;
        }

        sealed class SubmeshSource
        {
            public Color32[] Albedo;
            public int AlbedoWidth, AlbedoHeight;
            public TextureWrapMode Wrap;
            public Vector4 St = new Vector4(1, 1, 0, 0);
            public Color Color = Color.white;
            public bool Cutout;
            public float Cutoff = 0.5f;
            public Color32[] Emission;
            public int EmissionWidth, EmissionHeight;
            public Color EmissionColor;
            public bool HasEmission;
        }

        sealed class Prepared
        {
            public Target Target;
            public Renderer Clone;
            public Mesh Mesh;
            public List<Vector2> Uv0 = new List<Vector2>();
            public List<Vector2> UvLight = new List<Vector2>();
            public int[][] Triangles;
            public SubmeshSource[] Sources;
            public Rect LightBounds;
            public Vector2 Size;
        }

        /// <summary>焼き込みを実行し、結果の画素数を返す。</summary>
        public static Vector2Int Execute(GenerationContext gen, List<Target> targets, Func<int, (Color[] px, int w, int h)> lightmap, string operationId)
        {
            var settings = gen.Settings;
            bool linear = PlayerSettings.colorSpace == ColorSpace.Linear;
            float strength = Mathf.Clamp01(settings.lightBakeStrength);
            var textureCache = new Dictionary<Texture, (Color32[] px, int w, int h)>();
            var prepared = targets.Select(t => Prepare(gen, t, textureCache)).ToList();

            // 区画: ライトマップ UV の範囲を、元の色テクスチャのテクセル密度 × ワールド面積の画素数にする
            var pack = AtlasPacker.Pack(prepared.Select(p => p.Size).ToList(), Padding, settings.atlasMaxSize, allowRectangle: false);
            int size = pack.Width;
            var color = new Color32[size * size];
            var covered = new bool[size * size];
            bool anyEmission = prepared.Any(p => p.Sources.Any(s => s.HasEmission));
            Color32[] emission = anyEmission ? new Color32[size * size] : null;
            float emissionMax = 1f;
            if (anyEmission)
            {
                float ToLinear(float v) => linear ? Mathf.GammaToLinearSpace(v) : v;
                emissionMax = Math.Max(1e-4f, prepared.SelectMany(p => p.Sources).Where(s => s.HasEmission)
                    .Max(s => Math.Max(ToLinear(s.EmissionColor.r), Math.Max(ToLinear(s.EmissionColor.g), ToLinear(s.EmissionColor.b)))));
            }

            for (int i = 0; i < prepared.Count; i++)
            {
                gen.ThrowIfCancelled();
                var p = prepared[i];
                var lm = lightmap(p.Target.LightmapIndex);
                Rasterize(p, pack.Rects[i], size, lm, strength, linear, emissionMax, color, emission, covered);
            }
            var coveredForEmission = emission != null ? (bool[])covered.Clone() : null;
            Dilate(color, covered, size);
            if (emission != null) Dilate(emission, coveredForEmission, size);
            for (int i = 0; i < covered.Length; i++)
                if (!covered[i]) color[i].a = 255;

            var albedo = Save(gen, color, size, "BakedLitAlbedo", prepared.Any(p => p.Sources.Any(s => s.Cutout)), operationId);
            var emissionTexture = emission != null ? Save(gen, emission, size, "BakedLitEmission", false, operationId) : null;
            float emissionColor = linear ? Mathf.LinearToGammaSpace(emissionMax) : emissionMax;

            var shader = Shader.Find(LightmapBakeOperation.UnlitShaderName);
            if (shader == null) throw new InvalidOperationException("シェーダー " + LightmapBakeOperation.UnlitShaderName + " が見つかりません。");
            var materials = new Dictionary<(bool, float), Material>();
            Material MaterialFor(SubmeshSource s)
            {
                var key = (s.Cutout, s.Cutout ? s.Cutoff : 0f);
                if (materials.TryGetValue(key, out var m)) return m;
                m = new Material(shader) { name = "BakedLit" + (s.Cutout ? "_Cutout" : string.Empty) };
                m.SetTexture("_MainTex", albedo);
                m.SetColor("_Color", Color.white);
                m.SetFloat("_LightStrength", 0f); // 光は色に入っている
                if (emissionTexture != null)
                {
                    m.SetTexture("_EmissionMap", emissionTexture);
                    m.SetColor("_EmissionColor", new Color(emissionColor, emissionColor, emissionColor, 1));
                }
                if (s.Cutout)
                {
                    m.EnableKeyword("_ALPHATEST_ON");
                    m.SetFloat("_AlphaTest", 1);
                    m.SetFloat("_Cutoff", s.Cutoff);
                    m.renderQueue = 2450;
                }
                m = gen.AddAsset(m, "Materials", m.name, operationId, "lightmap-direct");
                materials[key] = m;
                return m;
            }

            for (int i = 0; i < prepared.Count; i++)
            {
                var p = prepared[i];
                var r = pack.Rects[i];
                var uv = p.UvLight.Select(u => Fit(p.LightBounds, r, u) / size).ToList();
                var copy = Object.Instantiate(p.Mesh);
                copy.name = p.Mesh.name + "_bakedlit";
                copy.SetUVs(0, uv);
                p.Clone.GetComponent<MeshFilter>().sharedMesh = gen.AddAsset(copy, "Meshes", copy.name, operationId, p.Target.Info.Key);
                p.Clone.sharedMaterials = p.Sources.Select(MaterialFor).ToArray();
                var flags = GameObjectUtility.GetStaticEditorFlags(p.Clone.gameObject);
                GameObjectUtility.SetStaticEditorFlags(p.Clone.gameObject, flags & ~StaticEditorFlags.ContributeGI);
            }
            return new Vector2Int(size, size);
        }

        static Prepared Prepare(GenerationContext gen, Target target, Dictionary<Texture, (Color32[] px, int w, int h)> cache)
        {
            var clone = gen.Copy.FindRenderer(target.Info.Key);
            if (clone == null) throw new InvalidOperationException("ワーキングコピー上の Renderer が見つかりません: " + target.Info.Key);
            var mesh = clone.GetComponent<MeshFilter>()?.sharedMesh;
            if (mesh == null) throw new InvalidOperationException("Mesh が見つかりません: " + target.Info.Key);
            var p = new Prepared { Target = target, Clone = clone, Mesh = mesh };
            mesh.GetUVs(0, p.Uv0);
            var lmUv = new List<Vector2>();
            mesh.GetUVs(mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord1) ? 1 : 0, lmUv);
            var so = target.ScaleOffset;
            p.UvLight = lmUv.Select(u => new Vector2(u.x * so.x + so.z, u.y * so.y + so.w)).ToList();
            if (p.Uv0.Count != mesh.vertexCount) p.Uv0 = Enumerable.Repeat(Vector2.zero, mesh.vertexCount).ToList();
            p.Triangles = Enumerable.Range(0, mesh.subMeshCount).Select(s => mesh.GetTopology(s) == MeshTopology.Triangles ? mesh.GetTriangles(s) : new int[0]).ToArray();
            var mats = clone.sharedMaterials;
            p.Sources = Enumerable.Range(0, mesh.subMeshCount).Select(s => Source(s < mats.Length ? mats[s] : null, cache)).ToArray();

            // ワールド面積と、元の色テクスチャの使用画素数（テクセル密度の見積もり）
            var vertices = mesh.vertices;
            var m = clone.transform.localToWorldMatrix;
            double world = 0, texels = 0;
            for (int s = 0; s < p.Triangles.Length; s++)
            {
                var src = p.Sources[s];
                var tris = p.Triangles[s];
                for (int t = 0; t + 2 < tris.Length; t += 3)
                {
                    var a = m.MultiplyPoint3x4(vertices[tris[t]]);
                    world += Vector3.Cross(m.MultiplyPoint3x4(vertices[tris[t + 1]]) - a, m.MultiplyPoint3x4(vertices[tris[t + 2]]) - a).magnitude * 0.5;
                    Vector2 u0 = Scale(p.Uv0[tris[t]], src.St), u1 = Scale(p.Uv0[tris[t + 1]], src.St), u2 = Scale(p.Uv0[tris[t + 2]], src.St);
                    texels += Math.Abs((u1.x - u0.x) * (u2.y - u0.y) - (u2.x - u0.x) * (u1.y - u0.y)) * 0.5 * src.AlbedoWidth * src.AlbedoHeight;
                }
            }
            float minX = p.UvLight.Min(u => u.x), minY = p.UvLight.Min(u => u.y), maxX = p.UvLight.Max(u => u.x), maxY = p.UvLight.Max(u => u.y);
            p.LightBounds = new Rect(minX, minY, Math.Max(1e-5f, maxX - minX), Math.Max(1e-5f, maxY - minY));
            // ライトマップ UV の範囲（縦横比）に、使用画素数ぶんの大きさを割り当てる（短い辺は最小 16px。縦横比は保つ）
            double pixels = Math.Max(256, texels);
            double aspect = Math.Max(1e-3, Math.Min(1e3, p.LightBounds.width / p.LightBounds.height));
            double w = Math.Sqrt(pixels * aspect), h = Math.Sqrt(pixels / aspect);
            double grow = Math.Max(1, 16 / Math.Min(w, h));
            p.Size = new Vector2((float)(w * grow), (float)(h * grow));
            return p;
        }

        /// <summary>ライトマップ UV の範囲を、縦横比を保ったまま区画に収める（一律の倍率・区画の中央）。</summary>
        static Vector2 Fit(Rect bounds, RectInt rect, Vector2 u)
        {
            float k = Math.Min(rect.width / bounds.width, rect.height / bounds.height);
            float ox = rect.x + (rect.width - bounds.width * k) * 0.5f, oy = rect.y + (rect.height - bounds.height * k) * 0.5f;
            return new Vector2(ox + (u.x - bounds.x) * k, oy + (u.y - bounds.y) * k);
        }

        static Vector2 Scale(Vector2 uv, Vector4 st) => new Vector2(uv.x * st.x + st.z, uv.y * st.y + st.w);

        static SubmeshSource Source(Material m, Dictionary<Texture, (Color32[] px, int w, int h)> cache)
        {
            var s = new SubmeshSource();
            if (m == null) return Solid(s);
            if (m.HasProperty("_MainTex") && m.GetTexture("_MainTex") is Texture2D main)
            {
                var px = Read(main, cache);
                s.Albedo = px.px;
                s.AlbedoWidth = px.w;
                s.AlbedoHeight = px.h;
                s.Wrap = main.wrapMode;
                var scale = m.GetTextureScale("_MainTex");
                var offset = m.GetTextureOffset("_MainTex");
                s.St = new Vector4(scale.x, scale.y, offset.x, offset.y);
            }
            else Solid(s);
            if (m.HasProperty("_Color")) s.Color = m.GetColor("_Color");
            s.Cutout = m.IsKeywordEnabled("_ALPHATEST_ON") || (m.renderQueue >= 2450 && m.renderQueue < 3000);
            if (m.HasProperty("_Cutoff")) s.Cutoff = m.GetFloat("_Cutoff");
            bool emission = (m.IsKeywordEnabled("_EMISSION") && m.shader.name.StartsWith("Standard", StringComparison.Ordinal)) ||
                            (m.HasProperty("_UseEmission") && m.GetFloat("_UseEmission") > 0.5f);
            if (emission && m.HasProperty("_EmissionColor"))
            {
                s.EmissionColor = m.GetColor("_EmissionColor");
                s.HasEmission = s.EmissionColor.maxColorComponent > 0;
                if (s.HasEmission && m.HasProperty("_EmissionMap") && m.GetTexture("_EmissionMap") is Texture2D map)
                {
                    var px = Read(map, cache);
                    s.Emission = px.px;
                    s.EmissionWidth = px.w;
                    s.EmissionHeight = px.h;
                }
            }
            return s;
        }

        static SubmeshSource Solid(SubmeshSource s)
        {
            s.Albedo = new[] { new Color32(255, 255, 255, 255) };
            s.AlbedoWidth = s.AlbedoHeight = 1;
            s.Wrap = TextureWrapMode.Clamp;
            return s;
        }

        /// <summary>読み取り可能ならそのまま、そうでなければ元ファイル（または GPU 経由）から読む。</summary>
        static (Color32[] px, int w, int h) Read(Texture2D texture, Dictionary<Texture, (Color32[] px, int w, int h)> cache)
        {
            if (cache.TryGetValue(texture, out var hit)) return hit;
            (Color32[] px, int w, int h) result;
            if (texture.isReadable) result = (texture.GetPixels32(), texture.width, texture.height);
            else if (TextureReader.TryInspect(texture, AtlasChannelKind.Color, out var info, out var reason))
            {
                var px = TextureReader.ReadPixels(info, out int w, out int h);
                result = (px, w, h);
            }
            else throw new InvalidOperationException("テクスチャを読み取れません（" + texture.name + "）: " + reason);
            cache[texture] = result;
            return result;
        }

        static void Rasterize(Prepared p, RectInt rect, int size, (Color[] px, int w, int h) lightmap, float strength, bool linear, float emissionMax,
            Color32[] color, Color32[] emission, bool[] covered)
        {
            Vector2 ToPixel(Vector2 u) => Fit(p.LightBounds, rect, u);
            float ToLinear(float v) => linear ? Mathf.GammaToLinearSpace(v) : v;
            float ToStored(float v) => linear ? Mathf.LinearToGammaSpace(Mathf.Clamp01(v)) : Mathf.Clamp01(v);

            for (int s = 0; s < p.Triangles.Length; s++)
            {
                var src = p.Sources[s];
                var tris = p.Triangles[s];
                for (int t = 0; t + 2 < tris.Length; t += 3)
                {
                    int i0 = tris[t], i1 = tris[t + 1], i2 = tris[t + 2];
                    Vector2 a = ToPixel(p.UvLight[i0]), b = ToPixel(p.UvLight[i1]), c = ToPixel(p.UvLight[i2]);
                    float area = (b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y);
                    if (Math.Abs(area) < 1e-8f) continue;
                    int x0 = Math.Max(rect.x - Padding, (int)Math.Floor(Math.Min(a.x, Math.Min(b.x, c.x))));
                    int x1 = Math.Min(rect.xMax + Padding - 1, (int)Math.Ceiling(Math.Max(a.x, Math.Max(b.x, c.x))));
                    int y0 = Math.Max(rect.y - Padding, (int)Math.Floor(Math.Min(a.y, Math.Min(b.y, c.y))));
                    int y1 = Math.Min(rect.yMax + Padding - 1, (int)Math.Ceiling(Math.Max(a.y, Math.Max(b.y, c.y))));
                    for (int y = Math.Max(0, y0); y <= Math.Min(size - 1, y1); y++)
                        for (int x = Math.Max(0, x0); x <= Math.Min(size - 1, x1); x++)
                        {
                            float px = x + 0.5f, py = y + 0.5f;
                            float w0 = ((b.x - px) * (c.y - py) - (c.x - px) * (b.y - py)) / area;
                            float w1 = ((c.x - px) * (a.y - py) - (a.x - px) * (c.y - py)) / area;
                            float w2 = 1 - w0 - w1;
                            const float eps = -0.01f;
                            if (w0 < eps || w1 < eps || w2 < eps) continue;
                            int index = y * size + x;
                            var uv0 = Scale(p.Uv0[i0] * w0 + p.Uv0[i1] * w1 + p.Uv0[i2] * w2, src.St);
                            var uvL = p.UvLight[i0] * w0 + p.UvLight[i1] * w1 + p.UvLight[i2] * w2;
                            var albedo = Bilinear(src.Albedo, src.AlbedoWidth, src.AlbedoHeight, uv0, src.Wrap);
                            var light = BilinearHdr(lightmap.px, lightmap.w, lightmap.h, uvL);
                            float Channel(float alb, float tint, float l)
                            {
                                float lit = Mathf.Lerp(1f, l, strength);
                                return ToStored(ToLinear(alb) * ToLinear(tint) * (linear ? lit : Mathf.Pow(lit, 1f / 2.2f)));
                            }
                            color[index] = new Color32(
                                (byte)Math.Round(Channel(albedo.r, src.Color.r, light.r) * 255),
                                (byte)Math.Round(Channel(albedo.g, src.Color.g, light.g) * 255),
                                (byte)Math.Round(Channel(albedo.b, src.Color.b, light.b) * 255),
                                (byte)Math.Round(Mathf.Clamp01(albedo.a * src.Color.a) * 255));
                            if (emission != null)
                            {
                                Color e = Color.black;
                                if (src.HasEmission)
                                {
                                    var map = src.Emission != null ? Bilinear(src.Emission, src.EmissionWidth, src.EmissionHeight, uv0, src.Wrap) : Color.white;
                                    e = new Color(
                                        ToStored(ToLinear(map.r) * ToLinear(src.EmissionColor.r) / emissionMax),
                                        ToStored(ToLinear(map.g) * ToLinear(src.EmissionColor.g) / emissionMax),
                                        ToStored(ToLinear(map.b) * ToLinear(src.EmissionColor.b) / emissionMax), 1);
                                }
                                emission[index] = e;
                            }
                            covered[index] = true;
                        }
                }
            }
        }

        static Color Bilinear(Color32[] px, int w, int h, Vector2 uv, TextureWrapMode wrap)
        {
            float fx = uv.x * w - 0.5f, fy = uv.y * h - 0.5f;
            int ix = Mathf.FloorToInt(fx), iy = Mathf.FloorToInt(fy);
            float tx = fx - ix, ty = fy - iy;
            int X(int v) => wrap == TextureWrapMode.Repeat ? ((v % w) + w) % w : Mathf.Clamp(v, 0, w - 1);
            int Y(int v) => wrap == TextureWrapMode.Repeat ? ((v % h) + h) % h : Mathf.Clamp(v, 0, h - 1);
            Color a = px[Y(iy) * w + X(ix)], b = px[Y(iy) * w + X(ix + 1)], c = px[Y(iy + 1) * w + X(ix)], d = px[Y(iy + 1) * w + X(ix + 1)];
            return Color.Lerp(Color.Lerp(a, b, tx), Color.Lerp(c, d, tx), ty);
        }

        static Color BilinearHdr(Color[] px, int w, int h, Vector2 uv)
        {
            float fx = uv.x * w - 0.5f, fy = uv.y * h - 0.5f;
            int ix = Mathf.FloorToInt(fx), iy = Mathf.FloorToInt(fy);
            float tx = fx - ix, ty = fy - iy;
            int X(int v) => Mathf.Clamp(v, 0, w - 1);
            int Y(int v) => Mathf.Clamp(v, 0, h - 1);
            Color a = px[Y(iy) * w + X(ix)], b = px[Y(iy) * w + X(ix + 1)], c = px[Y(iy + 1) * w + X(ix)], d = px[Y(iy + 1) * w + X(ix + 1)];
            return Color.LerpUnclamped(Color.LerpUnclamped(a, b, tx), Color.LerpUnclamped(c, d, tx), ty);
        }

        /// <summary>描いた画素の周囲を数画素ぶん近傍の色で埋める（ミップマップ・バイリニアで区画の外側が混ざって継ぎ目になるのを防ぐ）。</summary>
        static void Dilate(Color32[] px, bool[] covered, int size)
        {
            for (int pass = 0; pass < DilatePasses; pass++)
            {
                var next = (bool[])covered.Clone();
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        int i = y * size + x;
                        if (covered[i]) continue;
                        int r = 0, g = 0, b = 0, a = 0, n = 0;
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int nx = x + dx, ny = y + dy;
                                if (nx < 0 || ny < 0 || nx >= size || ny >= size) continue;
                                int j = ny * size + nx;
                                if (!covered[j]) continue;
                                r += px[j].r; g += px[j].g; b += px[j].b; a += px[j].a; n++;
                            }
                        if (n == 0) continue;
                        px[i] = new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), (byte)(a / n));
                        next[i] = true;
                    }
                Array.Copy(next, covered, covered.Length);
            }
        }

        static Texture2D Save(GenerationContext gen, Color32[] px, int size, string name, bool alpha, string operationId)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false, false) { name = name };
            texture.SetPixels32(px);
            texture.Apply(false, false);
            var record = new GeneratedTexture { OperationId = operationId, SourceRef = "lightmap-direct", HasAlpha = alpha };
            record.Import.SRgb = true;
            record.Import.MaxSize = size;
            return gen.AddTexture(texture, record, name);
        }
    }
}
