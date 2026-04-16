# NDMF MToon1.0 → lilToon Converter

MToon 1.0 / 互換 MToon マテリアルを lilToon へ変換するための Unity + NDMF 向けツールです。

## 概要

このツールは以下を目的としています。

- Build 時に NDMF フェーズで自動変換
- Editor 上での非破壊 Preview
- Hair マテリアルの選択的 merge / atlas
- 変換結果のレポート表示（件数・warning・unsupported）

## 前提

- Unity Editor
- NDMF
- lilToon

## 使い方

1. 対象アバターの Renderer を持つオブジェクトに `MToonLilToonComponent` を追加
2. `lilToon Shader` を設定
3. `Scan Materials` で候補収集
4. 必要に応じて Hair merge 対象を調整
5. `Preview` で変換後を確認（元オブジェクトは非破壊）
6. Build 時は NDMF plugin が自動適用

## Hair merge / atlas

- `HAIR`（大小無視）を含む名前のマテリアルを初期選択
- 候補の render type を集計し、多数派 type を merge 対象に採用
- atlas 対象: main / shade / emission / normal / outline
- UV は atlas rect に再配置し、サブメッシュ統合を実施

atlas サイズ方針:

- 21 枚以下: 原寸寄り（7×3 想定）
- 22 枚以上: 軽い縮小を許可し 32 枚（8×4）を優先

## Render Queue

- Opaque: `Geometry`
- Cutout: `AlphaTest`
- Transparent: **必ず `2460` 開始**
- Transparent は **元の MToon transparent 同士の相対順序のみ** を保持し、`2460 + rank` で**連番に詰めて再採番**
- VRoid などで `4000` 等の不適切な queue が入っていても、その絶対値は信用せず、lilToon/VRChat アバター運用向けに再構成
- この方針は **VRChat アバターで Focus が外れてボケる問題を避けるための仕様** であり、Unity 一般慣例より優先

### Render Queue ポリシー（変更禁止レベル）

- 本ツールは「Unity の一般的な queue 運用」ではなく、**VRChat でアバターに lilToon を使う実運用**を優先する。
- そのため Transparent queue は 2500/3000 帯へ分割しない。**2460 帯で密に採番**する。
- 将来の変更でも、このルールを崩す場合は「VRChat での実機検証結果」と「既存アバターへの影響評価」を必須とする。

## Inspector UI

- Preview ボタン（有効中は緑）
- Scan ボタン
- Hair 選択 UI
- レポート表示
- 日本語 / 英語切り替え
- lilToon ユーザー設定ラベル（境界の色、境界の幅、距離フェード、逆光ライト）

## 注意

- 実行環境外では実機見た目確認はできません
- 特殊なメッシュ構成では atlas/UV 結合結果の追加調整が必要な場合があります
- 変換時は lilToon の描画モード（Opaque/Cutout/Transparent）を **明示的に再設定** し、RenderType tag / clipping / alpha mode を同期する前提です。
- 影が消える事故を防ぐため、lilToon 側の shadow 有効系プロパティは存在する場合に有効化します（バージョン差異に対応するため複数候補を扱います）。
