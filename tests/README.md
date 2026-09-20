# Backfill checks

Build with `build.bat`, then run `powershell -NoProfile -ExecutionPolicy Bypass -File tests\backfill-regression.ps1`.

## Live capture

Keep WeChat logged in and unlocked. Capture takes foreground and scrolls history. Do not interact with WeChat during capture. No messages are sent.

Run executable with `--backfill-recent-now --chat CONTACT --report ABSOLUTE_JSON_PATH` to restrict shared backfill pipeline to one contact, regardless of last activity date. Without `--chat`, it scans session-list pages and selects chats active since last successful sweep's start date minus one day. First sweep defaults to yesterday. Unknown date formats are included conservatively; unread badges are never required. Compact chat view is navigated back to session list automatically. Chats absent from WeChat's session list still need separate discovery.

Capture requests 32000 pixels of window height, falling back to 16000/8000 if rejected. This is a tested ceiling, not an absolute Windows limit. Each chat restores prior window placement in cleanup. UIA provides offscreen text; only on-screen pixels can classify sender, otherwise sender stays unknown. Scroll targets are clipped to physical working area. Transfer-progress noise remains excluded.

Full-list discovery also uses oversized viewport with list paging. Single-contact tests navigate directly to their target. Keyboard navigation requires focus within target list, including its child rows. Reports include session-page count, candidate count, and anchor-stop count.

## Overnight operation

Keep daemon tray process running, Windows awake/unlocked, and WeChat logged in. Dashboard start/end controls configure window; default 01:00–06:00 after five idle minutes. A completed schedule is persisted in `night-backfill-state.json`; failures retry after fifteen minutes within schedule. Focus loss, held Escape, or schedule end interrupt capture. There is no automatic login, wake-from-sleep, or Windows-startup registration.

Night state records last attempt, error, and captured count. Completion state survives daemon restart. Schedule behavior has regression coverage; live overnight execution still needs validation with logged-in WeChat.

Exit code 1 and report `success: false` indicate failure. Exit code 0 with `success: true` means scan reached stable desktop-history boundary and saved its contiguous sequence. This does not prove phone-only messages have synced to desktop.

Live capture records visible desktop changes and never advances reconciliation checkpoints. Backfill jumps to latest and scrolls backward until unique ordered overlap with its own saved anchor. Version-2 anchors use up to eight non-media, non-timestamp messages and require at least three distinct texts. Legacy viewport checkpoints are ignored. First capture, missing anchors, and ambiguous overlaps require full scan or explicit failure. Saved later records cannot be discarded. A chat checkpoint advances only after successful save and restoration; global sweep time advances only when enumeration and every selected chat succeed. Individual chat errors allow other chats to proceed; interrupted/failed sweeps retain previous global cutoff.

Three unchanged reads determine history/list boundary, with 500 history-page and 1000 session-page limits. UI stalls can resemble boundaries, so this is not proof of complete desktop DB parity. Missing overlap, lost focus, conflicting saved history, unavailable contact, and concurrent capture fail explicitly. No partial scan replaces saved records.

## Independent comparison

`verify-backfill.ps1` takes `-RecordPath`, `-EvidencePath`, and optional `-PreviousRecordPath`. Evidence JSON must contain `records` array with chronological `text` fields from independent UI observations. Check compares exact text/order, unique IDs, stable IDs across repeated runs, and SHA-256 hash. It strips sender prefixes for text comparison; sender accuracy requires separate visual checks.

Repeated full scans must retain count and IDs. After sender enrichment settles, `changedContacts` should be zero and record-file hash unchanged. Checkpoint timestamp may still change.

Timestamp separators can change labels across days or disappear at different window heights. Full reconciliation preserves earlier separators and compares equivalent date labels using capture dates. Anchored reconciliation keeps historical prefix verbatim. This deliberately retains historical metadata instead of treating current UI layout as authoritative deletion evidence.

Run `tests\reconciliation-regression.ps1` for phone-only replies, missed-day activity selection, desktop-capture gap repair, repeated runs, and ambiguous-anchor safety checks.

Known limits: only chats in desktop session list are discoverable; no phone-sync control or explicit date-range selector; sender classification uses visible pixels; relative timestamp labels can conflict with older saved snapshots. Ambiguous overlap or saved-history conflicts stop that chat for review. Night schedule and multi-contact behavior need live checks with logged-in WeChat. Contact display names remain storage keys; collisions require stable account IDs in future.
