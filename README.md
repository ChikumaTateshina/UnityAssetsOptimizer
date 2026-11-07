# UnityAssetsOptimizer (UnityAssetsOptimizer)

VRChat のワールド/アバター向けの **非破壊** アセット最適化ツールです（Unity Editor 拡張）。
選択した GameObject と子階層に対してテクスチャアトラス化・Material 統合・テクスチャ圧縮を行い、
**原本を一切変更せず** 新しい Prefab を `Assets/UnityAssetsOptimizer/` に生成します。

- 検証状況: [CompatibilityMatrix.md](Packages/com.chikumatateshina.unityassetsoptimizer/Documentation~/CompatibilityMatrix.md)

## インストール（VCC / VPM）

1. 次のどちらかでリポジトリを VCC に追加します。
   - ブラウザで <https://chikumatateshina.github.io/UnityAssetsOptimizer/> を開き「VCC に追加」を押す
   - VCC の **Settings → Packages → Add Repository** に次の URL を入力する
     ```
     https://chikumatateshina.github.io/UnityAssetsOptimizer/index.json
     ```
2. VCC でプロジェクトの **Manage Project** を開き、**UnityAssetsOptimizer** を追加します。

対応: Unity 2022.3（Built-in Render Pipeline。VRChat 推奨の 2022.3.22f1 でテスト済み）。VRChat SDK へのコンパイル時依存はありません。

## 使い方

1. `Tools > UnityAssetsOptimizer > Open` でウィンドウを開く
2. Hierarchy / Project で対象を選択し「現在の選択を取り込む」（FBX などのモデルや Prefab もそのまま選べます）
3. プリセット（World / Avatar）と機能の ON/OFF を選ぶ
4. 解析と予測（処理前の値・予測値・処理ごとの判定とスキップ理由）は**自動で裏で実行**されます。
   対象や設定、シーン・アセットを変更すると再計算され、その間もエディタは通常どおり操作できます
5. 必要なら「出力先」を指定し（Prefab・Material・Mesh・Texture などの生成物がすべてそこに出力されます）、「最適化を実行」を押す（予測が最新のときだけ押せます）
6. 生成された Prefab を手動でシーンに配置して利用（元のオブジェクトはそのまま）

元に戻すには「履歴 / 復元」タブで **生成物を破棄** します。原本が外部要因で変更・削除された場合は、
**原本を検証** → **スナップショットから原本を書き戻す** で復元できます（スナップショット破損時は失敗を明示します）。

### 主な安全仕様

- 原本（シーン・Prefab・Mesh・Material・Texture）への書き込み禁止。書き込みはステージング領域に限定
- 未対応シェーダー・UV タイル・スクリプト参照・マテリアルアニメーション等は通常モードでスキップし、理由を表示
- 選択外のシーンオブジェクトへの参照や Missing Script がある場合は実行しない
- 失敗・キャンセル時は生成途中のデータを破棄（ロールバック）し、レポートを `Library/UnityAssetsOptimizer/Logs/` に保存
- 算出できない指標（Draw Calls 等）は「算出不可」と表示し、数値を補完しない
- Manual Override（`Tools > UnityAssetsOptimizer > Advanced > Enable Manual Override`）で FORCE を有効化できますが、
  原本保全・復元保証・処理境界は解除されません

### 実装状況（v0.9.0）

| 機能 | 状態 |
|---|---|
| 対象選択・依存解析・スナップショット・トランザクション・復元 | 実装済み |
| 近似 Material 統合（似た Material を表示し、選択したものだけ統合） | 実装済み |
| アトラス化 / 同一 Material 統合 / 重複 Texture 検出 | 実装済み（Unity 組み込みシェーダー、lilToon 2.3.1、Poiyomi Toon 10.0.29） |
| テクスチャ圧縮・解像度削減 | 実装済み（元のテクスチャはコピーを作って適用） |
| メッシュ結合（静的 / SkinnedMesh）・不要頂点属性削除 | 実装済み |
| Static Batching / GPU Instancing 支援・Lightmap UV 再生成 | 実装済み |
| ポリゴン削減・LOD 生成・Collider 簡略化 | 実装済み（誤差上限付き） |
| 再変換時の上書き（Prefab の GUID・内部 ID を維持） | 実装済み |
| Prefab Variant での出力 | 実装済み（Prefab Asset、または構造変更のないインスタンス） |
| 処理前 / 予測 / 処理後の比較、外観の参考比較（画像差分） | 実装済み（Draw Calls はモデル推定、フレーム時間は取得不可） |
| Avatar Performance Rank | VRChat SDK の公式評価 API で算出（PC / Android / iOS） |
| Android / iOS 派生版の生成 | 実装済み（SDK 3.9 系で検証。SDK の検証 API で判定、実機検証は対象外） |
| NDMF でのビルド時適用 | 実装済み（NDMF がある場合のみ有効） |
| AAO Trace And Optimize の設定 | 実装済み（Avatar Optimizer 1.8 系が入っている場合） |
| ビルド時ポリゴン削減（SkinnedMesh 可） | 実装済み（lilNDMFMeshSimplifier 0.3 系が入っている場合。目標は SDK の PC Poor 基準、顔は保護） |
| ビルド後（NDMF 適用後）の Rank 評価・ファイルサイズ計測 | 実装済み（Avatar Rank タブ） |
| Manual Override（FORCE・監査ログ） | 実装済み |
| Mesh Compression | 実装しない（誤差を検証できないため） |
| Poiyomi Pro / ロック済み Poiyomi | 未対応（Pro は未確認。ロック済みはロック解除してから実行） |

詳細は [CompatibilityMatrix.md](Packages/com.chikumatateshina.unityassetsoptimizer/Documentation~/CompatibilityMatrix.md) を参照してください。

### NDMF でビルド時に適用する

NDMF（Modular Avatar 等に同梱）が入っているプロジェクトでは、アバターのルートに
**Add Component → UnityAssetsOptimizer → Build Optimizer (NDMF)** を追加すると、
アップロード時・Play 時にビルド用のコピーにだけ最適化を適用します（元のアバターやシーンは変更しません）。
Avatar Optimizer（AAO）と併用する場合は、AAO より前に実行されます。

### 連携ツール

以下のツールが導入されていれば自動で使います（未導入ならウィンドウに「VCC に追加」ボタンが表示されます）。

- [Avatar Optimizer](https://vpm.anatawa12.com/)（anatawa12）: 未使用オブジェクト・BlendShape・PhysBone 等の最適化
- [lilNDMFMeshSimplifier](https://github.com/lilxyzw/lilNDMFMeshSimplifier)（lilxyzw）: ビルド時のポリゴン削減
- [NDMF](https://ndmf.nadena.dev/)（bd_）: ビルド後の構成での評価

## 開発

- パッケージ本体: `Packages/com.chikumatateshina.unityassetsoptimizer/`
- テスト: Unity の Test Runner（EditMode）。プロジェクトの `Packages/manifest.json` の `testables` にパッケージ名を追加してください（配布 zip にはテストを含めません）。VRChat SDK・lilToon・NDMF との連携テストは、それらが入ったプロジェクトでのみ実行されます
- CI: `.github/workflows/test.yml`（GameCI）。リポジトリの Secret に `UNITY_LICENSE`・`UNITY_EMAIL`・`UNITY_PASSWORD` を設定すると実行されます（未設定ならスキップ）

### リリース手順

1. `package.json` の `version` を更新し、`CHANGELOG.md` を追記
2. `git tag v<version>` を push
3. GitHub Actions の `release.yml` が zip を Release に添付し、完了後に `pages.yml` が VPM リスティングと紹介ページを GitHub Pages に公開します（`pages.yml` は Actions から手動実行もできます）

初回のみ、GitHub リポジトリの **Settings → Pages → Build and deployment → Source** を **GitHub Actions** に設定してください。

## ライセンス

[MIT License](LICENSE)
