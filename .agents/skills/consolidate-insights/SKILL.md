---
name: consolidate-insights
description: incident threading and profile routing for daemon insights. use daemon api jobs and preserve source references.
---

# consolidate insights

create incident/project/topic threads from related insights across days, and route enduring facts into profile fields.

profile vs insight routing MUST follow `docs/semantic_routing.md`.

## workflow

1. call `GET http://127.0.0.1:8081/wechat/daemon/jobs`.
2. process only jobs where `type` is `consolidation`.
3. for each job:
   - call `GET http://127.0.0.1:8081/wechat/daemon/jobs/<job id>`.
   - inspect `insights`, `existingThreads`, and `profile`.
   - group insights into threads only when they describe the same continuing incident, project, relationship issue, or topic.
   - route stable reusable contact facts into `profileUpdates`.
   - call `POST http://127.0.0.1:8081/wechat/daemon/jobs/<job id>/result`.
4. do not edit `profiles`, `chat_history.json`, `info.json`, or `scripts/insights_tmp` directly.

## result schema

```json
{
  "profileUpdates": {
    "BaseCity": "Beijing",
    "Company": "Tencent",
    "爱好": "网球"
  },
  "threads": [
    {
      "id": "optional existing thread id",
      "title": "Varvara art sales reliability issue",
      "category": "职业与项目",
      "summary": "thread-level summary in chinese",
      "childInsightIds": ["ins_id_1", "ins_id_2"],
      "sourceRecordIds": ["rec_id_1", "rec_id_2"],
      "highlightRecordIds": ["rec_id_2"],
      "latestDate": "2026-07-04"
    }
  ],
  "archiveInsightIds": ["ins_id_1", "ins_id_2"]
}
```

## rules

- do not merge unrelated events because they happened on the same day.
- `category` MUST be exactly one of: `职业与项目`, `生活与近况`, `偏好与兴趣`, `资源与合作`, `随便聊聊`.
- never output `Unknown`, empty strings, placeholder text, mojibake, or runs of `???` in `title`, `summary`, `category`, or profile values.
- before posting, validate every thread `summary` and `category`: if chinese text has become `?`, rebuild from child insight summaries instead of posting.
- do not translate category labels. use the exact chinese enum values above.
- output valid JSON only. keep all chinese characters as UTF-8 text; do not escape or down-convert them into replacement characters.
- profile is stable reusable context about the contact. insights and threads are relationship history: what the contact did, what i did, what we discussed, promised, sent, planned, rejected, resolved, or left open.
- if a stable contact fact appears inside a meaningful interaction, write both `profileUpdates` and a thread/insight summary when relevant.
- mention my action when it is useful relationship memory, but write `我` / `对方`; never write raw actor tokens like `[me]`, `[other]`, or `[system]` in summaries.
- use exact `id` values from job insights in `childInsightIds` and `archiveInsightIds`.
- use exact record ids from insight source fields in `sourceRecordIds` and `highlightRecordIds`.
- preserve insight dates. `latestDate` should come from child insight dates, not from current processing time.
- preserve existing thread ids when updating an existing thread.
