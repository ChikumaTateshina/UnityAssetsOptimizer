# VRChat Asset Optimizer (UnityAssetsOptimizer)

VRChat のワールド/アバター向けの **非破壊** アセット最適化ツールです（Unity Editor 拡張）。
選択した GameObject と子階層に対してテクスチャアトラス化・Material 統合・テクスチャ圧縮を行い、
**原本を一切変更せず** 新しい Prefab を `Assets/VAOGenerated/` に生成します。

- 設計文書: [要件定義書](docs/requirements.md) / [実装仕様書](docs/implementation_spec.md)
- 検証状況: [CompatibilityMatrix.md](Packages/com.chikumatateshina.unity-assets-optimizer/Documentation~/CompatibilityMatrix.md)

## インストール（VCC / VPM）

1. 次のどちらかでリポジトリを VCC に追加します。
   - ブラウザで <https://chikumatateshina.github.io/UnityAssetsOptimizer/> を開き「VCC に追加」を押す
   - VCC の **Settings → Packages → Add Repository** に次の URL を入力する
     ```
     https://chikumatateshina.github.io/UnityAssetsOptimizer/index.json
     ```
2. VCC でプロジェクトの **Manage Project** を開き、**VRChat Asset Optimizer** を追加します。

対応: Unity 2022.3（Built-in Render Pipeline）。VRChat SDK へのコンパイル時依存はありません。

## 使い方

1. `Tools > VRChat Asset Optimizer > Open` でウィンドウを開く
2. Hierarchy / Project で対象を選択し「現在の選択を取り込む」
3. プリセット（World / Avatar）と機能の ON/OFF を選ぶ
4. **解析** → 現在値と問題点を確認
5. **Dry Run** → 予測値・処理ごとの判定根拠・スキップ理由を確認
6. **最適化を実行** → 確認後、ステージングで生成・検証し、成功時のみコミット
7. 生成された Prefab を手動でシーンに配置して利用（元のオブジェクトはそのまま）

元に戻すには「履歴 / 復元」タブで **生成物を破棄** します。原本が外部要因で変更・削除された場合は、
**原本を検証** → **スナップショットから原本を書き戻す** で復元できます（スナップショット破損時は失敗を明示します）。

### 主な安全仕様

- 原本（シーン・Prefab・Mesh・Material・Texture）への書き込み禁止。書き込みはステージング領域に限定
- 未対応シェーダー・UV タイル・スクリプト参照・マテリアルアニメーション等は通常モードでスキップし、理由を表示
- 選択外のシーンオブジェクトへの参照や Missing Script がある場合は実行しない
- 失敗・キャンセル時は生成途中のデータを破棄（ロールバック）し、レポートを `Library/VAOLogs/` に保存
- 算出できない指標（Draw Calls 等）は「算出不可」と表示し、数値を補完しない
- Manual Override（`Tools > VRChat Asset Optimizer > Advanced > Enable Manual Override`）で FORCE を有効化できますが、
  原本保全・復元保証・処理境界は解除されません

### 実装状況（v0.1.0）

| 機能 | 状態 |
|---|---|
| 対象選択・依存解析・スナップショット・トランザクション・復元 | 実装済み |
| アトラス化 / 同一 Material 統合 / 重複 Texture 検出 | 実装済み（Unity 組み込みシェーダーのみ） |
| 生成テクスチャの圧縮・解像度制限 | 実装済み |
| 処理前 / 予測 / 処理後の指標比較、JSON・Markdown レポート | 実装済み |
| Manual Override（FORCE・監査ログ） | 実装済み |
| メッシュ結合・頂点属性整理・Mesh Compression | 未実装（Phase 2、常にスキップ） |
| 描画計測（Draw Calls 等）・Static Batching / GPU Instancing 支援 | 未実装（Phase 3） |
| Avatar Performance Rank | 統計値のみ。閾値 DB が未検証のためランクは算出不可 |
| ポリゴン削減・LOD・Collider 簡略化 | 未実装（Phase 4） |
| Android / iOS 派生版生成 | 未実装（Phase 5） |

## 開発

- パッケージ本体: `Packages/com.chikumatateshina.unity-assets-optimizer/`
- テスト: Unity の Test Runner（EditMode）。プロジェクトの `Packages/manifest.json` の `testables` にパッケージ名を追加してください

### リリース手順

1. `package.json` の `version` を更新し、`CHANGELOG.md` を追記
2. `git tag v<version>` を push
3. GitHub Actions（`.github/workflows/release.yml`）が zip を Release に添付し、VPM リスティングを GitHub Pages に公開します

初回のみ、GitHub リポジトリの **Settings → Pages → Build and deployment → Source** を **GitHub Actions** に設定してください。
