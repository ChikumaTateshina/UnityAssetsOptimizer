# VRChat Asset Optimizer — 実装仕様書

- 文書ID: VAO-IMP-001
- 版: 1.0（実装設計）
- 作成日: 2026-11-06
- 対応要件: `requirements.md`

## 1. 設計方針
Unity Editor専用の非破壊パイプラインとし、選択されたGameObject群から読み取り専用スナップショットを作成し、隔離した作業領域に生成物を作り、検証後に専用出力領域へコミットする。**元Scene、元Prefab、元Mesh/Texture/Materialの上書きを禁止**。FORCEでもこの不変条件は変更しない。

## 2. パッケージ構成
```text
Packages/com.example.vrc-asset-optimizer/
  package.json
  Runtime/                 # 必要最小限のデータ定義。実行時コードは原則不要
  Editor/
    UI/                    # EditorWindow, Inspector, Preview, Report
    Selection/             # SelectionResolver, DependencyGraph
    Analysis/              # AssetAnalyzer, MaterialAnalyzer, AvatarAnalyzer
    Planning/              # OptimizationPlanner, SafetyGate
    Operations/            # IOptimizationOperation, Texture, Material, Mesh
    Shaders/               # IShaderAdapter, adapter registry
    Platforms/             # CapabilityDB, Android, iOS, MobileConverter
    Profiling/             # Baseline, DryRun, Profiler, RankEstimator
    Recovery/              # Snapshot, Manifest, Transaction, Restore
    Validation/            # Structural, Visual, SDK, Regression
    Reporting/             # JSON/Markdown export
    Tests/                 # EditMode integration and regression tests
  Documentation~/
```
実際のpackage名は公開前に衝突確認する。VRChat SDKや第三者拡張への直接参照はoptional assembly/adapterに隔離する。

## 3. データモデル
```csharp
enum SupportState { Supported, Unsupported, Unknown }
enum ExecutionMode { Off, Auto, Force }
enum MetricKind { Calculated, Estimated, Measured, Unavailable }
enum RunState { Created, Analyzed, Planned, Previewed, Approved, Generating, Validating, Committed, RolledBack, Failed }
enum ConversionState { Verified, Partial, Incompatible }

record TargetRef(string GlobalObjectId, string AssetGuid, long LocalFileId);
record Metric(string Key, double? Value, string Unit, MetricKind Kind,
              string Method, string ContextId, string? UnavailableReason);
record OperationDecision(string OperationId, SupportState Support,
                         ExecutionMode Mode, string[] Reasons, string[] Dependencies);
record RunManifest(string RunId, string ToolVersion, string UnityVersion,
                   string SdkVersion, string InputHash, string SettingsHash,
                   TargetRef[] Targets, string[] OutputPaths,
                   OperationDecision[] Decisions, string SnapshotHash);
```
※C#レコードは概念モデル。Unity互換のシリアライズ実装ではclass/struct + JSON DTO等に置換可能。`GlobalObjectId`とGUID/localFileIDを併用し、インスタンス・Prefab Asset・Prefab Variantを区別する。

## 4. 処理パイプライン
1. **Resolve**: `Selection.gameObjects`から重複する子選択を除外。対象階層、Prefab依存、外部参照を収集。
2. **Snapshot**: 元Scene/Prefab/アセットの識別子・内容ハッシュ・参照グラフを記録し、必要な元データの独立コピーを作成。検証前に先へ進まない。
3. **Analyze**: Mesh/Renderer/Material/Texture/Shader/Animator/Udon/VRChatコンポーネント等を走査。未知依存は`Unknown`。
4. **Plan**: 各Operationが前提・依存・競合・期待効果・安全判定を返す。グラフをトポロジカルソート。AUTOはSupportedのみ、FORCEは確認付きで一部Unknown/Unsupportedを許可。ただし操作の実行可能性と復元性は必須。
5. **Dry Run**: 一時領域で同一アルゴリズムの生成を行い、構造値を計算。描画系はプレビュー環境で参考測定。実行前に現在値と予測値を表示。
6. **Approve**: リスク、予測の信頼性、スキップ理由、生成先を提示し明示確認。
7. **Generate**: 一時領域に派生Mesh/Material/Texture/Prefabを生成。
8. **Validate**: 構造・参照・描画・SDK（利用可能時）を検証。失敗時はロールバック。
9. **Commit**: 完全な出力セットを専用フォルダへ移動・登録。Manifestとレポートを確定。
10. **Post-measure**: 同一条件で再測定し、現在・予測・結果と予測誤差を表示。

キャンセル/例外時は一時生成物だけを破棄する。UnityのAssetDatabase操作はメインスレッド制約を遵守。EditorSceneManagerで元Sceneにdirtyを付けないことをテストする。

## 5. Operationインターフェース
```csharp
interface IOptimizationOperation {
    string Id { get; }
    OperationCapabilities Capabilities { get; }
    AnalysisResult Analyze(ReadOnlyAssetGraph graph, OptimizationSettings settings);
    OperationPlan Plan(AnalysisResult analysis, OptimizationSettings settings);
    GeneratedAssets Execute(GenerationContext isolatedContext, OperationPlan plan);
    ValidationResult Validate(GeneratedAssets output, OperationPlan plan);
}
```
- `Capabilities`: World/Avatar、Static/Skinned、シェーダー、プラットフォーム、必要UV、BlendShape、Lightmap、Animation等。
- 各Operationは書き込み可能パスのallowlistを宣言。外部変更を要求したら拒否。
- 同一アセットへの競合操作は依存関係を明示し、順序を固定。
- FORCEはOperation側が`CanForce`を宣言した場合のみ選択可能。未知データを勝手に解釈するフォールバック禁止。

## 6. テクスチャアトラス化
### 6.1 互換グループ判定
Shader Adapter ID/Version、Render Queue、Blend Mode、Cull、ZWrite、Keyword、プロパティセット、UVチャンネル、テクスチャWrap/Filter、動的Material操作を比較する。条件が不明ならグループから除外。UVのタイル・ミラー・負座標は対応アルゴリズムがなければスキップ。

### 6.2 生成
1. 各Materialの必要テクスチャチャンネルと色空間を抽出。
2. UVの使用領域とTexel Densityを計算し、padding/mip bleedを考慮して矩形パック。
3. Base Color、Normal、Mask、Emission等の対応チャンネルを整合したレイアウトで合成。
4. Meshを複製し、SubMesh単位のUVを変換。境界頂点は複製し、法線/接線/ウェイト/BlendShape等の頂点属性を保持。
5. Materialを複製して新しいAtlas参照を設定。対応するSubMeshを統合。
6. RendererのMaterial配列と参照を新Prefab内だけで差し替える。
7. UV0以外、ライトマップスケール/オフセット、特殊シェーダー処理を検証。必要ならスキップ。

`Texture2D.PackTextures`は利用可能だが、画質・サイズ・レイアウト再現性の制御が不十分なら決定的な独自パッカーを使用する。GPU上の圧縮容量はプラットフォームのImporter設定から算出し、実際の常駐VRAMと混同しない。

## 7. その他の最適化
- **重複検出**: テクスチャは内容ハッシュだけでなくsRGB/NormalMap/Alpha/Importer設定を含む等価性判定。Materialは全有効プロパティと描画状態の等価性判定。
- **圧縮/解像度**: コピーに対してPlatformTextureSettingsを適用。品質差のある処理は明示設定に従い検証。
- **Mesh結合**: 同じ描画特性、静的状態、ライトマップ、空間近接、外部参照なしを前提。SkinnedMeshRendererは初期OFF。
- **頂点属性整理**: シェーダー/ライトマップ/Animation参照から未使用を証明できた属性のみ削除。
- **Mesh Compression**: 圧縮後の誤差を測定。安全閾値外なら不採用。
- **LOD/Decimation**: Phase 4。UV/法線/BlendShape/ボーン保護。元Meshは保持。閾値超過時は不採用。
- **Collider**: Colliderの機能互換性を維持できる場合のみ。Udon参照が不明ならスキップ。
- **Batching/Instancing**: 競合関係をPlannerが判定。Draw Call減少を保証せず測定比較。

## 8. 安全判定とManual Override
通常モードは`Supported`のみ実行。`Unsupported`/`Unknown`はスキップし理由を表示。隠しメニュー `Tools > VRChat Asset Optimizer > Advanced > Enable Manual Override` を用意し、EditorPrefs等でユーザー単位の表示設定を保持。各処理にOFF/AUTO/FORCEを表示。

FORCE実行前には、対象、非互換リスク、予測値の信頼性、影響範囲を表示し、個別に承認を要求。次は常に禁止: 元アセットの直接編集、選択外への書き込み、復元用データの省略、検証不能な参照書換え、必須SDK制約を無視した「対応済み」表示、技術的に未実装の処理。FORCEで生成できても検証失敗なら未検証生成物として隔離するかロールバックし、正常完了と表示しない。

## 9. メトリクスと予測
### 9.1 計測コンテキスト
`ContextId`にはUnity/SDK/Build Target、Quality、Render Pipeline、Scene、Camera、解像度、ライト、Batching、可視状態、サンプル数、Profiler有効状態を記録。比較時に不一致なら警告。

### 9.2 指標別方式
| 指標 | 現在値 | 予測値 | 事後 |
|---|---|---|---|
| Triangles/Vertices | Meshから計算 | Dry Run生成Meshから計算 | 生成Meshから計算 |
| Renderer/Material Slots | 階層から計算 | 生成階層から計算 | 生成階層から計算 |
| Texture GPUメモリ | 形式・Mip・解像度から推定 | 生成Importer設定から推定 | 生成物から推定、可能ならProfiler計測 |
| Draw Calls/SetPass/Batches | 対応する描画計測があれば実測 | 隔離プレビュー測定/モデル推定 | 同一条件で測定 |
| CPU/GPU Frame Time | Profiler計測可能時のみ | 推定困難なら取得不可 | 実機/Editorの計測を環境別表示 |
| Build Size | 既存成果物があれば実測 | 圧縮・生成アセットから推定 | 実際のBuild出力があれば測定 |

選択GameObjectに対するDraw Callはバッチングにより帰属が曖昧な場合がある。`isolated-preview`または`scene-delta`と明記し、元シーンの実測値と偽らない。事前画面は現在値と予測値を必須表示するが、根拠がない場合は「算出不可」を許容する。

## 10. VRChat Avatar Performance Rank
- `IVrcRankProvider`を定義し、`SdkOfficialProvider`（利用可能な公開APIのみ）と`VersionedEstimateProvider`を実装。
- 公式閾値はSDK/プラットフォーム/評価ルール版に紐付けたデータとして管理し、固定ハードコードしない。
- PC/Android/iOSのランク、各項目の値、閾値、超過量、制約項目、目標ランク差を表示。
- `official`/`estimated`/`incomplete`/`unavailable`を明示。
- 非アクティブオブジェクト、ビルド時変換、外部拡張の生成要素も評価対象として検討。最終ビルド構成を取得できなければ完全評価と表示しない。
- 公式の判定が取れない場合に独自推定値を公式結果として提示しない。

## 11. Mobile Compatibility Converter（Phase 5）
### 11.1 ターゲット
`WindowsOriginal`、`AndroidVariant`、`iOSVariant`を別々に管理。World/Avatarごとの制約を適用。SDKで対応確認できないプラットフォームは変換を無効化。

### 11.2 Capability Database
```json
{
  "schemaVersion": 1,
  "unityVersionRange": "VERIFIED_RANGE_REQUIRED",
  "vrcSdkVersionRange": "VERIFIED_RANGE_REQUIRED",
  "platform": "Android",
  "contentType": "Avatar",
  "shaderAdapterIds": [],
  "textureFormats": [],
  "performanceRuleSetId": "VERIFIED_RULESET_REQUIRED",
  "verified": false
}
```
これはスキーマ例であり、未検証の能力値を実データとして採用しない。AndroidとiOSは独立したDB/テストマトリクスとする。

### 11.3 変換ステップ
1. 対応SDK/Unity/Platformの照合。
2. 参照・ギミック・Shader・Texture・Avatar/World固有コンポーネントの互換性解析。
3. 安全なTexture/Material/Mesh処理を再利用。
4. 対応Shader Adapterのみでモバイル向けMaterialを生成。透明・屈折・輪郭等の非等価変換はAUTO禁止。
5. Android/iOS別のTexture Importer設定と派生Prefab生成。
6. SDKの公開された検証/ビルド処理を実行可能な範囲で呼び出す。
7. `Verified`（SDK検証済）、`Partial`、`Incompatible`を判定。実機検証済みかは別フラグ。
8. アップロードはユーザー確認後、SDK公式フローを利用。初期実装はビルド・検証までとし、公開自動化は最後に追加。

### 11.4 注意点
- モバイルアバターのShader制約とワールドのShader制約を混同しない。
- iOSのサポート状況はSDK版で確認。Androidの成功結果をiOS成功とみなさない。
- Modular Avatar/VRCFury等のビルド時変換を無条件に書き換えない。
- 非対応ギミックを削除して見かけ上ビルド成功にすることは禁止（FORCEでの明示的な変更は別扱い）。

## 12. UI仕様
メインEditorWindow:
1. 選択対象一覧と解析範囲
2. World/Avatarプリセット選択
3. 各機能ON/OFFと詳細パラメータ
4. 現在値/予測値、根拠、信頼区分、スキップ理由
5. Dry Run/最適化実行/キャンセル
6. 実行後の結果値、予測誤差、変更履歴、復元
7. Avatar Rankタブ（PC/Android/iOS）
8. Mobile Conversionタブ（Android/iOS個別選択）

Manual Override UIは通常非表示。詳細レポートには操作単位のSupportState/Modeを表示。結果はCSVではなくまずJSONとMarkdownで出力。

## 13. 永続化・復元
```text
Assets/VAOGenerated/<run-id>/
  Manifest.json
  Snapshot/                 # 独立した原本コピー・参照情報
  Generated/                # 最適化派生アセット
  Android/                  # Phase 5
  iOS/                      # Phase 5
  Reports/
```
- 出力フォルダの競合を避け、原本と別GUIDで生成。
- 生成物に元参照ID・ハッシュ・処理IDを関連付ける。
- 復元は元オブジェクトへの切替/生成物の破棄を基本とする。ユーザーが生成物をシーンへ手動配置した後の外部変更は自動的に元へ戻せるとは限らないため、影響範囲を検査し、参照の変更を確認する。
- 元データの独立コピーが不足する場合は復元保証を宣言しない。必要容量を事前提示。
- Manifestはバージョン付き。ハッシュ照合失敗時は自動復元せずエラーを報告。

## 14. テスト計画
### Unit / EditMode
- 複数選択・親子重複・Prefab Variant・Nested Prefab。
- 同一Material判定、UVタイル/ミラー/負座標、Normal/Mask/Emissionのアトラス一致。
- SubMesh/BlendShape/SkinnedMeshRenderer/Animator/Udon参照保護。
- 未知Shader/Component/外部参照時のfail-closed。
- AUTOとFORCEの判定、不可解除の不変条件。
- 途中キャンセル、例外、ディスク不足、Manifest破損、GUID衝突。

### Integration
- 元Scene/Prefab/Texture/Meshの内容ハッシュ・GUIDが前後不変。
- Dry Runと本実行で生成構造の整合性。
- World/Avatar別の標準プリセット。
- Performance RankのSDK由来/推定/不完全のラベル。
- Android/iOS別のImporter設定・SDKビルド検証。
- 同一測定条件での前後メトリクス比較。

### Visual/Device
- 複数カメラ・照明・Animation状態で画像差分。画像差分のみで機能同等と断定しない。
- Android実機/iOS実機の検証は機器とSDKサポートが揃う場合のみ。

## 15. 実装マイルストーンと完了条件
| Phase | 作業 | 完了条件 |
|---|---|---|
| 1 | Selection、Snapshot、Transaction、Material/Atlas | 原本不変、復元テスト、事前・事後構造値 |
| 2 | Texture/Mesh最適化 | スキップ判定、競合検出、容量比較 |
| 3 | Planner、Profiler、Rank、Manual Override | 予測の根拠、計測コンテキスト、FORCE監査 |
| 4 | Decimation、LOD、Collider | 形状・参照保護、差分検証 |
| 5 | Android/iOS Converter | プラットフォーム別生成、SDK検証、復元 |

## 16. Codexへの実装指示
- 各Phaseの開始時に対象Unity/VRChat SDKバージョンの公開APIと公式仕様を検証し、結果を`CompatibilityMatrix.md`に記録する。
- 未検証のAPI/Shader/プラットフォーム能力を実装済みと仮定しない。
- 最初にRecovery/Transaction/SafetyGateとテストを実装し、その後に最適化アルゴリズムを追加する。
- すべてのOperationに対応判定・Dry Run・検証・ロールバックを必須化する。
- 本文の例示コード・データはコンパイル可能な完成コードではなく、設計契約として扱う。
- 原本変更、未知データの推測変換、数値の捏造、未検証の成功表示は禁止。
