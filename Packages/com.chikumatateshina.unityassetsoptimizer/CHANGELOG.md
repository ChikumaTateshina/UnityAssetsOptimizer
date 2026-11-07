# Changelog

## [0.5.0] - 2026-11-07

### Added
- 解析と予測（Dry Run）を自動で裏側で実行。対象・設定・シーン/アセットの変更を検知して再計算し、エディタの操作を妨げない
  （処理はフレームごとに時間を区切って進め、ファイルのハッシュ計算・アトラスの合成・ポリゴン削減の計算は別スレッドで実行）
- Prefab の出力先フォルダを指定可能に（依存アセットとスナップショットは管理フォルダに残す。破棄時は出力先の Prefab も削除）
- 設定をプロジェクトごとに保存

### Changed
- 「解析」「Dry Run」ボタンを廃止し、進捗をウィンドウ内に表示
- 未実装の機能と算出しない指標（CPU/GPU 時間）を UI に表示しない
- FBX などのモデル Prefab 内のオブジェクトも結合できるように修正
  （アニメーションの判定を、実際に使われている AnimationClip だけで行う。モデルに内蔵されているだけの Clip は判定に使わない）
- Unity 2022.3.22f1（VRChat の推奨版）でテスト

## [0.4.0] - 2026-11-07

### Changed
- 名称を UnityAssetsOptimizer に統一
  - パッケージ名: `com.chikumatateshina.unity-assets-optimizer` → `com.chikumatateshina.unityassetsoptimizer`（旧名は `legacyPackages` で置き換え）
  - 表示名・メニュー（`Tools > UnityAssetsOptimizer`）・名前空間/アセンブリ（`ChikumaTateshina.UnityAssetsOptimizer.*`）
  - 生成先 `Assets/UnityAssetsOptimizer/`、ログ `Library/UnityAssetsOptimizer/Logs/`、閾値 DB `Assets/UnityAssetsOptimizerSettings/RankRules/`
  - NDMF コンポーネント: `UnityAssetsOptimizer > Build Optimizer (NDMF)`
- 旧バージョンの生成物（`Assets/VAOGenerated/`）は自動では移動しません。不要なら削除してください

## [0.3.0] - 2026-11-07

### Added
- Poiyomi Toon 10.0.29（無料版）の Shader Adapter。全テクスチャのサンプル方法をソースから抽出して分類し、UV 選択・パン・Stochastic・全体 UV 設定・視差等の前提条件を検証
- Poiyomi（Opaque）→ VRChat/Mobile/Toon Standard の変換規則（非等価のため FORCE のみ）
- ロック済みシェーダー（Hidden/Locked/...）に対する専用のスキップ理由

### Changed
- テクスチャチャンネルごとの前提条件（そのテクスチャが設定されている場合のみ評価）に対応

## [0.2.0] - 2026-11-07

### Added
- lilToon 2.3.1 の Shader Adapter（メイン UV 系テクスチャのアトラス化。UV 非依存テクスチャの同一性・UV 関連機能の前提条件を検証）
- メッシュ結合（静的: 空間セル単位 / SkinnedMesh: 設定で有効化。アニメーション・参照・他コンポーネントを持つものは対象外）
- Static Batching 支援、GPU Instancing 支援（Material をコピーして有効化）、Lightmap UV 再生成
- 不要頂点属性の削除（シェーダーアダプターが使用属性を宣言している場合のみ）
- ポリゴン削減（QEM / half-edge collapse。誤差上限・境界・UV 継ぎ目・Material 境界を保護）、LOD 生成、MeshCollider 簡略化
- 元テクスチャの圧縮・解像度削減（コピーを生成して適用。元の Importer は変更しない）
- Prefab Variant 出力（Prefab Asset、または構造変更のないインスタンスの場合）
- VRChat SDK 公式評価 API による Avatar Performance Rank（PC / Android / iOS）
- Android / iOS 派生版の生成（SDK のシェーダー検証 API で判定、非等価変換は FORCE のみ）
- NDMF ビルド時適用（`Build Optimizer (NDMF)` コンポーネント、NDMF がある場合のみ有効）
- Draw Calls / SetPass / Batches のモデル推定、外観の参考比較（画像差分）
- テスト追加、GitHub Actions でのテスト（GameCI、Secret 未設定時はスキップ）

### Changed
- 各処理の直後にその処理の検証を行う順序に変更
- 構造検証は、各処理が登録した構造変更（結合で外した Renderer、LOD の追加など）だけを許容する

### Not implemented
- Mesh Compression（誤差を検証できないため）

## [0.1.0] - 2026-11-07

### Added
- 非破壊パイプライン、スナップショット、トランザクション、復元
- アトラス化（Unity 組み込みシェーダー）、同一 Material 統合、重複 Texture 検出、生成テクスチャの圧縮
- 指標の比較、JSON / Markdown レポート、Manual Override
