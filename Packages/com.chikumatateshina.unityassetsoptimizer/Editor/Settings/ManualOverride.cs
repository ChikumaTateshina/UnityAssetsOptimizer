using UnityEditor;

namespace ChikumaTateshina.UnityAssetsOptimizer
{
    /// <summary>
    /// Manual Override（隠しモード）。EditorPrefs に保存されるためユーザー・マシン単位で有効化される。
    /// FORCE は互換性・外観の自動判定のみを緩和し、原本保全・復元保証・処理境界・トランザクションは解除しない。
    /// </summary>
    public static class ManualOverride
    {
        const string PrefKey = "ChikumaTateshina.UnityAssetsOptimizer.ManualOverride.Enabled";
        const string MenuPath = UnityAssetsOptimizerInfo.MenuRoot + "Advanced/Enable Manual Override";

        public static bool IsEnabled => EditorPrefs.GetBool(PrefKey, false);

        [MenuItem(MenuPath, false, 2000)]
        static void Toggle()
        {
            if (IsEnabled)
            {
                EditorPrefs.SetBool(PrefKey, false);
                return;
            }

            var ok = EditorUtility.DisplayDialog(
                "Manual Override を有効化",
                "FORCE モードは互換性・外観保持の自動判定を緩和します。\n\n" +
                "・原本の直接編集、選択外への書き込み、復元データの省略は FORCE でも行いません。\n" +
                "・FORCE 実行時は機能ごとに個別承認を求め、監査ログに記録します。\n" +
                "・検証に失敗した生成物は正常完了として扱いません。\n\n有効化しますか？",
                "有効化", "キャンセル");
            if (ok) EditorPrefs.SetBool(PrefKey, true);
        }

        [MenuItem(MenuPath, true)]
        static bool ToggleValidate()
        {
            Menu.SetChecked(MenuPath, IsEnabled);
            return true;
        }
    }
}
