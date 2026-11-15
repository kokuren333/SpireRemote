# SpireBridge 実機ロードテスト（次回、ユーザー立ち会い時のみ）

この資料とスクリプトは準備物です。今回の Codex 作業では **Deploy / Verify とも実行しません**。
基準は `bridge/DESIGN.md` §6、SpireBridge 0.2.0 / ゲーム v0.113.1。
ゲームの起動・終了・操作はユーザーが手動で行います。スクリプトはゲームを起動・終了せず、Verify は GET /health と GET /state のみを送ります。

## 配置 → 起動

- [ ] プレイとゲームの終了をユーザーが確認する。作業中にゲームを起動しない。
- [ ] 将来の配置作業として `powershell -ExecutionPolicy Bypass -File .\loadtest\loadtest.ps1 -Phase Deploy` を実行する。
- [ ] ゲーム稼働中なら BLOCKED となり、コピーされないことを確認する。
- [ ] 停止中なら `C:\Users\nekom\SpireBridge\dist\SpireBridge\` の DLL/JSON 2ファイルだけがゲームの `mods\SpireBridge\` にコピーされ、各 SHA256 が PASS になることを確認する。コピー元が足りなければ中断してビルド担当へ戻す。
- [ ] ユーザーがゲームを通常起動する。必要ならゲーム内で mod を有効にする。
- [ ] `godot.log` の `[SpireBridge] Ready` と通常起動を目視確認する。ログファイルはスクリプトが自動探索・編集しない。
- [ ] 将来の検証作業として `powershell -ExecutionPolicy Bypass -File .\loadtest\loadtest.ps1 -Phase Verify` を実行する。
- [ ] `/health` の待機上限は120秒。service=SpireBridge、version=0.2.0、game_version=v0.113.1 を確認する。版が違う場合は無条件に成功扱いせず、基準との差として記録する。
- [ ] `results/<日時>/01-load-health.json` と `01-load-state.json` を確認する。`screen: unknown` が継続するなら状態取得失敗として記録し、ゲームスレッド捕捉の調査へ戻す。フェイクやハーネスを実機の代わりに成功扱いしない。

## メニュー → マップ → 戦闘 → ポーション

各ステップでユーザーが画面を用意し、Enter で採取します。各採取は `<step>-health.json` と `<step>-state.json` を保存します。
画面・JSON の自動判定と、目視/手動操作の PASS / FAIL / SKIP は `summary.json` に別々に残ります。
該当画面・ポーションに到達できなければ SKIP と理由を追記し、検証済みとはしません。

| 順序 | 採取ファイルの接頭辞 | ユーザーの操作と照合点 |
|---|---|---|
| [ ] メニュー | `02-menu` | screen=main_menu、run/player=null、配列は空。実際のメニュー画面と一致する。 |
| [ ] マップ | `03-map` | ラン開始後、移動前に screen=map、Act/フロア/ゴールド、choices の次ノードと順序を照合。最新 `/actions` の choose_map を手動実行し、移動を確認する。 |
| [ ] 戦闘 | `04-combat` | screen=combat。HP/最大HP/ブロック/エナジー、全カードのコスト・説明数値・使用可否、敵HP/インテント/攻撃合計値を照合。対象なし・対象ありカードを各1回手動実行し、実際の効果を確認する。end_turn を手動実行し、EndPlayerTurnAction / SetReadyToEndTurn の経路をログで確認する。 |
| [ ] 戦闘中ポーション・使用前 | `05-potion-combat-before` | スロット、名称、対象、最新アクションが一致することを確認。 |
| [ ] 戦闘中ポーション・使用後 | `05-potion-combat-after` | ユーザーが使用して消費・対象・効果を照合。 |

手動アクション検証には、状態パネルかローカルの API クライアントを用い、**その時点の `/actions` が返す action_id をそのまま**使います。
手札や画面が変わったら取り直してください。Verify スクリプトは `/do` を実行しません。
検証前後の追加 JSON が必要なら、表示中画面を再採取して保存してください。

## 報酬 → イベント / 休憩 → 終了確認

| 順序 | 採取ファイルの接頭辞 | ユーザーの操作と照合点 |
|---|---|---|
| [ ] 報酬 | `06-rewards` | screen=rewards と choices を報酬画面に照合。choices が空なら reflection 取得失敗/未対応として記録。claim_reward の効果を手動で確認。 |
| [ ] カード報酬遷移 | `06-card-selection` | screen=card_selection への遷移を記録。v0.2.0 はカード選択操作未対応なので、選択は手動で完了。 |
| [ ] イベント前後 | `07-event-before` / `07-event-after` | choices の label とロック状態を照合。現在の choose_event を手動実行し、進行後の状態を採取。 |
| [ ] 休憩前後 | `07-rest-before` / `07-rest-after` | label と使用可否を照合。現在の choose_rest を手動実行し、効果と遷移を採取。 |
| [ ] 戦闘外ポーション前後 | `07-potion-outside-before` / `07-potion-outside-after` | 使用可能な種類がある場合に、戦闘外使用の消費・効果を照合。なければ SKIP。 |
| [ ] 終了直前 | `08-before-exit` | 最終状態 JSON と関連ログを保存。 |
| [ ] 通常終了 | `08-after-exit-health` / `08-after-exit-state` | ユーザーが通常終了。スクリプトは接続不能も到達性の記録として保存する。応答消失だけで正常終了と断定せず、画面・godot.log の目視結果も残す。 |

- [ ] スクリプトの各ステップ表示と `summary.json` の FAIL / SKIP を確認する。画面種別一致だけではカード効果や選択進行の証明にならないため、目視判定も必須。
- [ ] 異常時はログと JSON を残してユーザーに報告。切り分けの mod 除去は、**ゲームをユーザーが終了した後、別途明示された作業としてのみ**行う。このスクリプトには削除処理はない。
- [ ] DESIGN.md の例を実測へ差し替える場合は、担当者が採取結果をレビューしてから行う。未取得項目を架空の値で埋めない。
- [ ] PWA の映像サイズ/FPS/音声帯域や操作ラグは別途実機で測定する。今回の静的チェックとハーネス検証を実ゲーム性能の測定と混同しない。
