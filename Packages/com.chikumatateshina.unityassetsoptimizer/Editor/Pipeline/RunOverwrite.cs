using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace ChikumaTateshina.UnityAssetsOptimizer
{
    /// <summary>
    /// 再変換時に前回の出力を上書きする。前回の Prefab は GUID を保ったまま新しい内容に置き換え（シーン等からの参照が切れない）、
    /// 前回の Material・Mesh・Texture 等は、ほかのアセットから参照されていなければ削除する。
    /// 手順: Prepare（前回の出力を退避）→ 新しい生成物の配置 → ReplacePrefabs → Finish（退避分を削除し前回の Manifest を更新）。
    /// 途中で失敗したら Undo で退避した出力を元の場所・内容に戻す。
    /// </summary>
    public sealed class RunOverwrite
    {
        const string TrashFolderName = "_Overwritten";

        sealed class Moved
        {
            public OutputAsset Output;
            public string OriginalPath;
            public string TrashPath;
            public byte[] OriginalBytes; // 上書きした Prefab の元の内容（Undo 用）
        }

        public RunRecord Previous;
        readonly List<Moved> _moved = new List<Moved>();
        readonly List<OutputAsset> _kept = new List<OutputAsset>();
        string _trash;
        int _replacedPrefabs;

        public int ReplacedPrefabs => _replacedPrefabs;
        public int RemovedAssets => _moved.Count(m => !IsPrefab(m.Output)) ;
        public IReadOnlyList<OutputAsset> Kept => _kept;

        static bool IsPrefab(OutputAsset o) => o.kind == "Prefab" || o.kind == "PrefabVariant";

        /// <summary>同じ対象（集合として一致）・同じ種類（PC / Android / iOS）で、生成物が残っている最新の実行。</summary>
        public static RunRecord FindPrevious(IList<TargetRef> targets, RunKind kind)
        {
            var ids = new HashSet<string>(targets.Select(t => t.globalObjectId));
            if (ids.Count == 0 || ids.Any(string.IsNullOrEmpty) || ids.Any(IsUnsavedObjectId)) return null;
            return RestoreService.FindRuns()
                .Where(r => r.Error == null && r.Manifest.state == RunState.Committed && KindOf(r.Manifest) == kind)
                .Where(r => ids.SetEquals(r.Manifest.targets.Select(t => t.globalObjectId)))
                .FirstOrDefault(r => r.Manifest.outputs.Any(o => IsPrefab(o) && Exists(o)));
        }

        /// <summary>未保存のシーンのオブジェクトは ID が安定しないため照合しない。</summary>
        static bool IsUnsavedObjectId(string id) => id.Contains("-00000000000000000000000000000000-");

        static RunKind KindOf(RunManifest m)
        {
            if (!string.IsNullOrEmpty(m.runKind) && Enum.TryParse(m.runKind, out RunKind k)) return k;
            // runKind を記録していない版の Manifest は Prefab 名の接尾辞で判定する
            var prefab = m.outputs.FirstOrDefault(IsPrefab)?.path ?? string.Empty;
            foreach (RunKind kind in Enum.GetValues(typeof(RunKind)))
                if (kind != RunKind.Optimize && Path.GetFileNameWithoutExtension(prefab).EndsWith("_" + kind, StringComparison.Ordinal)) return kind;
            return RunKind.Optimize;
        }

        static bool Exists(OutputAsset o) => !string.IsNullOrEmpty(o.guid) && AssetDatabase.GUIDToAssetPath(o.guid) == o.path;

        static string Full(string assetPath) => Path.Combine(UnityAssetsOptimizerInfo.ProjectRoot, assetPath);

        /// <summary>前回の出力を退避する（新しい生成物が同じ名前を使えるように）。ほかから参照されているものは動かさない。</summary>
        public void Prepare(IEnumerable<string> excludeFromReferenceScan)
        {
            var outputs = Previous.Manifest.outputs.Where(Exists).ToList();
            var referenced = ReferencedFromElsewhere(outputs, excludeFromReferenceScan);
            _trash = Previous.RunFolder + "/" + TrashFolderName;
            AssetPathUtility.EnsureFolder(_trash);
            int n = 0;
            foreach (var o in outputs)
            {
                if (!IsPrefab(o) && referenced.Contains(o.guid))
                {
                    _kept.Add(o);
                    continue;
                }
                var trashPath = _trash + "/" + (n++) + "_" + Path.GetFileName(o.path);
                var err = AssetDatabase.MoveAsset(o.path, trashPath);
                if (!string.IsNullOrEmpty(err)) throw new OptimizerAbortException("前回の出力を退避できません: " + err);
                _moved.Add(new Moved { Output = o, OriginalPath = o.path, TrashPath = trashPath });
            }
        }

        /// <summary>前回の出力の GUID のうち、前回の生成物以外（シーン・Prefab・Material 等）から参照されているもの。</summary>
        static HashSet<string> ReferencedFromElsewhere(List<OutputAsset> outputs, IEnumerable<string> exclude)
        {
            var own = new HashSet<string>(outputs.Select(o => o.path).Concat(exclude ?? Enumerable.Empty<string>()));
            var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { ".unity", ".prefab", ".mat", ".asset", ".controller", ".overrideController", ".anim", ".playable", ".mask" };
            var scan = AssetDatabase.GetAllAssetPaths()
                .Where(p => p.StartsWith("Assets/", StringComparison.Ordinal) && !own.Contains(p) && extensions.Contains(Path.GetExtension(p)))
                .ToArray();
            var deps = new HashSet<string>(AssetDatabase.GetDependencies(scan, false).Select(AssetDatabase.AssetPathToGUID));
            return new HashSet<string>(outputs.Select(o => o.guid).Where(deps.Contains));
        }

        /// <summary>新しい Prefab を、前回の Prefab（GUID）に内容を書き込む形で置き換える。outputs と generatedPrefabs のパスは変わらない。</summary>
        public void ReplacePrefabs(List<OutputAsset> newOutputs)
        {
            foreach (var o in newOutputs.Where(IsPrefab))
            {
                var old = _moved.FirstOrDefault(m => IsPrefab(m.Output) && m.Output.sourceRef == o.sourceRef && m.OriginalBytes == null);
                if (old == null) continue;
                // 内部 ID（fileID）を前回の Prefab に揃える。揃えないとシーンに置いたインスタンスが作り直され、
                // インスタンスへの参照やインスタンス側の上書きが失われる
                var bytes = Encoding.UTF8.GetBytes(RemapFileIds(File.ReadAllText(Full(o.path)),
                    AssetDatabase.LoadAssetAtPath<GameObject>(o.path), AssetDatabase.LoadAssetAtPath<GameObject>(old.TrashPath)));
                old.OriginalBytes = File.ReadAllBytes(Full(old.TrashPath));
                if (!AssetDatabase.DeleteAsset(o.path)) throw new OptimizerAbortException("Prefab を置き換えられません: " + o.path);
                var err = AssetDatabase.MoveAsset(old.TrashPath, o.path);
                if (!string.IsNullOrEmpty(err)) throw new OptimizerAbortException("前回の Prefab を移動できません: " + err);
                old.TrashPath = o.path;
                File.WriteAllBytes(Full(o.path), bytes);
                AssetDatabase.ImportAsset(o.path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                o.guid = old.Output.guid;
                _replacedPrefabs++;
            }
        }

        static readonly Regex FileIdPattern = new Regex(@"(?<=&|fileID: )-?\d+", RegexOptions.Compiled);

        /// <summary>
        /// 新しい Prefab の YAML の内部 ID を、階層パス・コンポーネントの型・同じ型の中での順番が一致する前回のオブジェクトの ID に置き換える。
        /// 一致しないオブジェクトは新しい ID のまま。Variant でも PrefabInstance の ID が揃うことで、派生する ID も揃う。
        /// </summary>
        public static string RemapFileIds(string yaml, GameObject newRoot, GameObject oldRoot)
        {
            if (newRoot == null || oldRoot == null) return yaml;
            var oldIds = new Dictionary<string, long>();
            foreach (var pair in Objects(oldRoot)) oldIds[pair.Key] = pair.Value;
            var map = new Dictionary<string, string>();
            foreach (var pair in Objects(newRoot))
                if (oldIds.TryGetValue(pair.Key, out var oldId) && oldId != pair.Value)
                    map[pair.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)] = oldId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            // Variant の PrefabInstance（GameObject/Component ではない）もルートと同じ扱いで揃える
            var newInstance = PrefabInstanceId(newRoot);
            var oldInstance = PrefabInstanceId(oldRoot);
            if (newInstance != 0 && oldInstance != 0) map[newInstance.ToString(System.Globalization.CultureInfo.InvariantCulture)] = oldInstance.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (map.Count == 0) return yaml;
            if (new HashSet<string>(map.Values).Count != map.Count) return yaml; // 対応が一意でなければ揃えない
            return FileIdPattern.Replace(yaml, m => map.TryGetValue(m.Value, out var v) ? v : m.Value);
        }

        static IEnumerable<KeyValuePair<string, long>> Objects(GameObject root)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                var path = AssetPathUtility.HierarchyPath(t, root.transform);
                if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(t.gameObject, out string _, out long goId))
                    yield return new KeyValuePair<string, long>(path + "|GameObject", goId);
                foreach (var group in t.GetComponents<Component>().Where(c => c != null).GroupBy(c => c.GetType()))
                {
                    int i = 0;
                    foreach (var c in group)
                        if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(c, out string _, out long id))
                            yield return new KeyValuePair<string, long>(path + "|" + group.Key.FullName + "#" + i++, id);
                }
            }
        }

        static long PrefabInstanceId(GameObject root)
        {
            var instance = PrefabUtility.GetPrefabInstanceHandle(root);
            return instance != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(instance, out string _, out long id) ? id : 0;
        }

        /// <summary>退避した前回の出力を削除し、前回の Manifest を「上書き済み」にする。</summary>
        public void Finish(string newRunId)
        {
            foreach (var m in _moved.Where(m => m.OriginalBytes == null)) AssetDatabase.DeleteAsset(m.TrashPath);
            if (AssetDatabase.IsValidFolder(_trash)) AssetDatabase.DeleteAsset(_trash);
            var manifest = Previous.Manifest;
            var folders = manifest.outputs.Select(o => Path.GetDirectoryName(o.path)?.Replace('\\', '/')).Where(d => !string.IsNullOrEmpty(d)).Distinct().ToList();
            manifest.state = RunState.Overwritten;
            manifest.overwrittenBy = newRunId;
            manifest.outputs = new List<OutputAsset>(_kept);
            manifest.outputPaths = _kept.Select(o => o.path).ToList();
            File.WriteAllText(Path.Combine(Full(Previous.RunFolder), UnityAssetsOptimizerInfo.ManifestFileName), JsonUtility.ToJson(manifest, true), new UTF8Encoding(false));
            // 前回の実行フォルダ内の空になった生成物フォルダだけを片付ける（出力先フォルダは利用者のものなので残す）
            AssetPathUtility.DeleteEmptyFolders(folders.Where(f => AssetPathUtility.IsUnder(f, Previous.RunFolder)));
        }

        /// <summary>失敗時: 退避した出力を元の場所と内容に戻す。</summary>
        public void Undo()
        {
            foreach (var m in Enumerable.Reverse(_moved))
            {
                if (m.OriginalBytes != null) File.WriteAllBytes(Full(m.TrashPath), m.OriginalBytes);
                if (m.TrashPath != m.OriginalPath) AssetDatabase.MoveAsset(m.TrashPath, m.OriginalPath);
                if (m.OriginalBytes != null) AssetDatabase.ImportAsset(m.OriginalPath, ImportAssetOptions.ForceUpdate);
            }
            if (_trash != null && AssetDatabase.IsValidFolder(_trash)) AssetDatabase.DeleteAsset(_trash);
        }
    }
}
