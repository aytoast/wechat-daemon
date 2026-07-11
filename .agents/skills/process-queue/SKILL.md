---
name: process-wechat-queue
description: process pending wechat conversations from the daemon queue. use when the user asks to process pending wechat chats, analyze backlog, or generate insights for contacts.
---

# process wechat queue

analyze daemon review jobs and submit semantic results through the daemon api.

## scope
- default: process pending chats for all contacts.
- if the user names a specific contact, process that contact only.

## workflow

1. call `GET http://127.0.0.1:8081/wechat/daemon/jobs`.
2. if `count` is `0`, stop.
3. for each job:
   - call `GET http://127.0.0.1:8081/wechat/daemon/jobs/<job id>`.
   - analyze `records`, `existingInsights`, and `profile`.
   - call `POST http://127.0.0.1:8081/wechat/daemon/jobs/<job id>/result` with the result schema below.
4. do not edit `profiles`, `chat_history.json`, `info.json`, or `scripts/insights_tmp` directly.
5. do not run `detect_overlaps.py` or `apply_merges.py` in this hourly queue workflow.

## json schema

the extracted insights must adhere to this exact json array format:

```json
{
  "replaceInsightIds": [],
  "newInsights": [
    {
      "summary": "summary of the conversation in chinese. focus on key facts, preferences, or resources mentioned.",
      "category": "职业与项目",
      "date": "yyyy-mm-dd from source record CapturedAt when present",
      "sourceRecordIds": ["rec_id_from_job_records"],
      "highlightRecordIds": ["most_relevant_rec_id"]
    }
  ],
  "markRecordIdsProcessed": ["rec_id_from_job_records"],
  "markRecordIdsSkipped": [],
  "profileUpdates": {}
}
```

## rules
- `category` MUST be exactly one of: `职业与项目`, `生活与近况`, `偏好与兴趣`, `资源与合作`, `随便聊聊`, `忽略`.
- never output `Unknown`, empty strings, placeholder text, mojibake, or runs of `???` in `summary`, `category`, or profile values.
- before posting, validate every persisted `summary` and `category`: if chinese text has become `?`, regenerate from source records instead of posting.
- do not translate category labels. use the exact chinese enum values above.
- output valid JSON only. keep all chinese characters as UTF-8 text; do not escape or down-convert them into replacement characters.
- if a job contains purely meaningless banter, emojis, or very short logistical updates with no value, do not persist an insight. put those record ids in `markRecordIdsSkipped`.
- do not analyze hidden metadata as content. records with `Type` values like `timestamp`, `media`, `call`, `system`, or `gap` are only ordering/context markers. `scripts/get_pending_chats.py` should hide or auto-process them.
- profile vs insight routing MUST follow `docs/semantic_routing.md`.
- profile is stable reusable context about the contact. insights are dated relationship history: what the contact did, what i did, what we discussed, promised, sent, planned, rejected, resolved, or left open.
- if a stable contact fact appears inside a meaningful interaction, write both `profileUpdates` and `newInsights`.
- mention my action when it is useful relationship memory, but write `我` / `对方`; never write raw actor tokens like `[me]`, `[other]`, or `[system]` in summaries.
- `sourceRecordIds` MUST use exact `Id` values from the job `records`.
- `highlightRecordIds` should include 1 to 3 record ids that best explain the insight.
- date insights from source record `CapturedAt` when present. visible timestamp records are context only, not standalone proof for old backfill.
- chunking: if a contact has more than 100 pending messages, break them down into chunks to maintain high extraction quality.
- do not hallucinate messages. every source id must come from the job payload.
