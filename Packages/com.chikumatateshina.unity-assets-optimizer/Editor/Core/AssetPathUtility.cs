using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace ChikumaTateshina.AssetsOptimizer
{
    public static class AssetPathUtility
    {
        /// <summary>Unity 組み込みリソース（ファイル実体を持たず書き換えもできない）。</summary>
        public static bool IsBuiltin(string assetPath)
        {
            return string.IsNullOrEmpty(assetPath) ||
                   assetPath.StartsWith("Resources/unity_builtin_extra", StringComparison.Ordinal) ||
                   assetPath.StartsWith("Library/", StringComparison.Ordinal);
        }

        /// <summary>AssetDatabase パスをディスク上のフルパスへ変換する。解決できなければ null。</summary>
        public static string ToFullPath(string assetPath)
        {
            if (IsBuiltin(assetPath)) return null;
            if (assetPath == "Assets" || assetPath.StartsWith("Assets/", StringComparison.Ordinal))
                return Path.GetFullPath(Path.Combine(VaoInfo.ProjectRoot, assetPath));

            if (assetPath.StartsWith("Packages/", StringComparison.Ordinal))
            {
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(assetPath);
                if (info == null || string.IsNullOrEmpty(info.resolvedPath)) return null;
                var prefix = "Packages/" + info.name;
                var rest = assetPath.Length > prefix.Length ? assetPath.Substring(prefix.Length + 1) : string.Empty;
                return Path.GetFullPath(Path.Combine(info.resolvedPath, rest));
            }
            return null;
        }

        public static bool IsUnder(string assetPath, string folder)
        {
            return assetPath == folder || assetPath.StartsWith(folder + "/", StringComparison.Ordinal);
        }

        /// <summary>Assets 配下のフォルダを（必要なら入れ子で）作成する。</summary>
        public static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            var parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
            var name = Path.GetFileName(folder);
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name))
                throw new InvalidOperationException("Invalid folder path: " + folder);
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, name);
        }

        public static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "unnamed";
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (var c in name) sb.Append(Array.IndexOf(invalid, c) >= 0 || c == '/' || c == '\\' ? '_' : c);
            var result = sb.ToString().Trim();
            if (result.Length > 64) result = result.Substring(0, 64);
            return result.Length == 0 ? "unnamed" : result;
        }

        /// <summary>
        /// 兄弟インデックス付きの階層パス（例: "Body[0]/Mesh[2]"）。同名兄弟がいても一意になる。
        /// </summary>
        public static string HierarchyPath(Transform t, Transform root)
        {
            if (t == root) return string.Empty;
            var sb = new StringBuilder();
            var current = t;
            while (current != null && current != root)
            {
                var segment = current.name + "[" + current.GetSiblingIndex() + "]";
                sb.Insert(0, sb.Length == 0 ? segment : segment + "/");
                current = current.parent;
            }
            return sb.ToString();
        }

        /// <summary>兄弟インデックスなしの名前パス（AnimationClip のバインディングパスと同形式）。</summary>
        public static string NamePath(Transform t, Transform root)
        {
            if (t == root) return string.Empty;
            var sb = new StringBuilder();
            var current = t;
            while (current != null && current != root)
            {
                sb.Insert(0, sb.Length == 0 ? current.name : current.name + "/");
                current = current.parent;
            }
            return sb.ToString();
        }

        public static string DisplayPath(GameObject go)
        {
            if (go == null) return "(null)";
            var sb = new StringBuilder(go.name);
            var t = go.transform.parent;
            while (t != null)
            {
                sb.Insert(0, t.name + "/");
                t = t.parent;
            }
            if (EditorUtility.IsPersistent(go)) return AssetDatabase.GetAssetPath(go) + ":" + sb;
            return (string.IsNullOrEmpty(go.scene.path) ? "(未保存シーン)" : go.scene.path) + ":" + sb;
        }

        public static long FileSize(string assetPath)
        {
            var full = ToFullPath(assetPath);
            if (full == null) return 0;
            var info = new FileInfo(full);
            return info.Exists ? info.Length : 0;
        }
    }
}
