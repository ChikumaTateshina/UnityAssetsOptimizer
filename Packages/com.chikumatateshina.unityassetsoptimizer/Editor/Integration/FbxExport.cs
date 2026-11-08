using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ChikumaTateshina.UnityAssetsOptimizer
{
    /// <summary>
    /// Unity 公式の FBX Exporter（com.unity.formats.fbx）で変換結果を FBX として書き出し、
    /// 静的メッシュは FBX 内のメッシュへ付け替える（Blender 等で編集して保存すると、Unity が再インポートして Prefab に反映される）。
    /// 付け替えは三角形数・サブメッシュ数・UV の組数・大きさが一致する場合のみ。SkinnedMesh はボーンの並びが変わり得るため付け替えない。
    /// </summary>
    public static class FbxExport
    {
        public sealed class Result
        {
            public string FbxPath;
            public int Relinked;
            /// <summary>FBX のメッシュに付け替えたため不要になった可能性のあるメッシュの .asset（他の Prefab が使っていなければ削除する）。</summary>
            public readonly List<string> RemovedMeshAssets = new List<string>();
            public readonly List<string> Notes = new List<string>();
        }

        public static bool IsAvailable(out string reason) => ExternalTools.FbxExporter.IsAvailable(out reason);

        static MethodInfo ExportMethod()
        {
            var type = ExternalTools.FbxExporter.ResolveType(ExternalTools.FbxExporter.MainType);
            return type?.GetMethod("ExportObject", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(Object) }, null);
        }

        /// <summary>GameObject を FBX ファイル（絶対パスまたは Assets からのパス）に書き出す。</summary>
        public static void Export(GameObject root, string path)
        {
            var method = ExportMethod();
            if (method == null) throw new MissingMethodException("FBX Exporter の ModelExporter.ExportObject(string, Object) が見つかりません。");
            var full = Path.IsPathRooted(path) ? path : Path.Combine(UnityAssetsOptimizerInfo.ProjectRoot, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            var written = method.Invoke(null, new object[] { full, root }) as string;
            if (string.IsNullOrEmpty(written) || !File.Exists(full)) throw new InvalidOperationException("FBX の書き出しに失敗しました: " + path);
        }

        /// <summary>Prefab を書き出す（Prefab の中身を一時的に開いて書き出す）。</summary>
        public static void ExportPrefab(string prefabPath, string fbxPath)
        {
            var contents = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                Export(contents, fbxPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        /// <summary>
        /// 生成した Prefab を同じフォルダへ FBX として書き出し、静的メッシュを FBX のメッシュへ付け替える。
        /// generatedMeshes はこの実行で作ったメッシュのアセットパス（付け替えたものは削除する）。
        /// </summary>
        public static Result ExportAndRelink(string prefabPath, ICollection<string> generatedMeshes)
        {
            var result = new Result();
            var folder = Path.GetDirectoryName(prefabPath).Replace('\\', '/');
            var fbxPath = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + Path.GetFileNameWithoutExtension(prefabPath) + ".fbx");
            ExportPrefab(prefabPath, fbxPath);
            AssetDatabase.ImportAsset(fbxPath, ImportAssetOptions.ForceSynchronousImport);
            ConfigureImporter(fbxPath);
            result.FbxPath = fbxPath;

            var model = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            if (model == null)
            {
                result.Notes.Add("FBX を読み込めないため、メッシュは付け替えません。");
                return result;
            }
            // FBX の一番上のノードがモデルのルートになる場合と、ファイル名のルートの下に入る場合がある。名前のパスが一意なものだけ対応させる
            Dictionary<string, Transform> Index(Transform root) => root.GetComponentsInChildren<Transform>(true)
                .GroupBy(t => FbxPath(t, root), StringComparer.Ordinal).Where(g => g.Count() == 1)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var candidates = new List<Dictionary<string, Transform>> { Index(model.transform) };
            if (model.transform.childCount == 1) candidates.Add(Index(model.transform.GetChild(0)));

            var contents = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                var prefabPaths = contents.GetComponentsInChildren<MeshFilter>(true).Select(f => FbxPath(f.transform, contents.transform)).ToList();
                var modelByPath = candidates.OrderByDescending(c => prefabPaths.Count(c.ContainsKey)).First();
                var replaced = new HashSet<Mesh>();
                var kept = new HashSet<Mesh>();
                foreach (var filter in contents.GetComponentsInChildren<MeshFilter>(true))
                {
                    var mesh = filter.sharedMesh;
                    if (mesh == null || !generatedMeshes.Contains(AssetDatabase.GetAssetPath(mesh))) continue;
                    var path = AssetPathUtility.NamePath(filter.transform, contents.transform);
                    var fbxMesh = modelByPath.TryGetValue(FbxPath(filter.transform, contents.transform), out var node) ? node.GetComponent<MeshFilter>()?.sharedMesh : null;
                    var mismatch = fbxMesh == null ? "FBX に対応するメッシュがありません" : Mismatch(mesh, fbxMesh);
                    if (mismatch != null)
                    {
                        kept.Add(mesh);
                        result.Notes.Add(path + ": " + mismatch + "（.asset のまま使います）");
                        continue;
                    }
                    filter.sharedMesh = fbxMesh;
                    foreach (var collider in filter.GetComponents<MeshCollider>().Where(c => c.sharedMesh == mesh)) collider.sharedMesh = fbxMesh;
                    replaced.Add(mesh);
                    result.Relinked++;
                }
                int skinned = contents.GetComponentsInChildren<SkinnedMeshRenderer>(true).Count(s => s.sharedMesh != null && generatedMeshes.Contains(AssetDatabase.GetAssetPath(s.sharedMesh)));
                if (skinned > 0) result.Notes.Add("SkinnedMesh " + skinned + " 個は、FBX 側でボーンの並びが変わり得るため .asset のまま使います（FBX は編集用のコピー）。");
                PrefabUtility.SaveAsPrefabAsset(contents, prefabPath);
                // 付け替えたメッシュの .asset は不要（他から参照されているものは残す）
                foreach (var mesh in replaced.Where(m => !kept.Contains(m)))
                {
                    var assetPath = AssetDatabase.GetAssetPath(mesh);
                    if (contents.GetComponentsInChildren<SkinnedMeshRenderer>(true).Any(s => s.sharedMesh == mesh)) continue;
                    result.RemovedMeshAssets.Add(assetPath);
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
            // 削除は呼び出し側で、すべての Prefab を処理した後に参照されていないものだけ行う
            return result;
        }

        /// <summary>
        /// FBX のノード名の規則に揃えた名前のパス（英数字と _ 以外は _ に置き換わり、数字で始まる名前は先頭に _ が付く）。
        /// 例: "1FEV-inner" → "_1FEV_inner"
        /// </summary>
        static string FbxPath(Transform t, Transform root)
        {
            var parts = new List<string>();
            for (var cur = t; cur != null && cur != root; cur = cur.parent) parts.Insert(0, FbxName(cur.name));
            return string.Join("/", parts);
        }

        static string FbxName(string name)
        {
            var chars = name.Select(c => (c < 128 && char.IsLetterOrDigit(c)) || c == '_' ? c : '_').ToArray();
            var safe = new string(chars);
            return safe.Length > 0 && char.IsDigit(safe[0]) ? "_" + safe : safe;
        }

        /// <summary>往復用のインポート設定（Material を作らない、頂点の最適化・圧縮なし、法線・接線は書き出した値）。</summary>
        static void ConfigureImporter(string fbxPath)
        {
            if (!(AssetImporter.GetAtPath(fbxPath) is ModelImporter importer)) return;
            importer.materialImportMode = ModelImporterMaterialImportMode.None;
            importer.importAnimation = false;
            importer.importCameras = false;
            importer.importLights = false;
            importer.importBlendShapes = true;
            importer.importNormals = ModelImporterNormals.Import;
            importer.importTangents = ModelImporterTangents.Import;
            importer.meshCompression = ModelImporterMeshCompression.Off;
            importer.optimizeMeshPolygons = false;
            importer.optimizeMeshVertices = false;
            importer.weldVertices = false;
            importer.generateSecondaryUV = false;
            importer.isReadable = true;
            importer.SaveAndReimport();
        }

        /// <summary>付け替えてよいか（三角形数・サブメッシュ数・UV の組数・大きさ）。問題がなければ null。</summary>
        static string Mismatch(Mesh a, Mesh b)
        {
            if (a.subMeshCount != b.subMeshCount) return "サブメッシュ数が異なります（" + a.subMeshCount + " → " + b.subMeshCount + "）";
            long Tris(Mesh m) => Enumerable.Range(0, m.subMeshCount).Sum(s => (long)m.GetIndexCount(s) / 3);
            // FBX の読み込みでは面積 0 の三角形が捨てられることがあるため、わずかな差は許す
            if (Math.Abs(Tris(a) - Tris(b)) > Math.Max(2, Tris(a) / 200)) return "三角形数が異なります（" + Tris(a) + " → " + Tris(b) + "）";
            for (int ch = 0; ch < 4; ch++)
            {
                var attr = UnityEngine.Rendering.VertexAttribute.TexCoord0 + ch;
                if (a.HasVertexAttribute(attr) != b.HasVertexAttribute(attr)) return "UV" + ch + " の有無が異なります";
            }
            float tolerance = Math.Max(1e-4f, a.bounds.size.magnitude * 0.02f);
            if ((a.bounds.center - b.bounds.center).magnitude > tolerance || (a.bounds.size - b.bounds.size).magnitude > tolerance)
                return "大きさ・位置が異なります（" + a.bounds + " → " + b.bounds + "）";
            if (a.blendShapeCount != b.blendShapeCount) return "BlendShape 数が異なります";
            // FBX の読み込みでは法線・接線が自動で作られる。不要な頂点属性を削った最適化を戻さないよう、属性の有無が同じ場合だけ付け替える
            foreach (var attr in new[] { UnityEngine.Rendering.VertexAttribute.Normal, UnityEngine.Rendering.VertexAttribute.Tangent, UnityEngine.Rendering.VertexAttribute.Color })
                if (a.HasVertexAttribute(attr) != b.HasVertexAttribute(attr)) return attr + " の有無が異なります（FBX の読み込みで追加・削除される）";
            return null;
        }
    }
}
