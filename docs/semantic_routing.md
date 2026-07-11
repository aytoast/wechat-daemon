# semantic routing mechanism

## purpose

route every extracted fact to exactly one durable home:

- `profile`: stable facts about the contact.
- `insights`: dated interaction history between me and the contact.
- `threads`: consolidated multi-insight stories built from related insights.

## core split

`profile` answers: who is this person, what is true about them, what do they generally prefer, what persistent context should be remembered before future conversations.

`insights` answers: what happened between us, what the contact did, what i did, what was discussed, promised, sent, asked, planned, rejected, resolved, or left open.

## profile rules

write to `profileUpdates` only when a fact is stable, reusable, and about the contact.

profile examples:

- identity: name, alias, pronouns, birthday, age, hometown, base city, languages.
- work: company, role, project, industry, current affiliation, investor type.
- contact context: how we met, relationship to me, referral source, mutual contacts.
- preferences: food, travel style, communication style, hobbies, disliked topics.
- long-running constraints: budget range, availability pattern, dietary restriction, visa status.
- resources owned by the contact: venues, companies, media channels, capital, suppliers, client network.
- recurring traits: reliable, slow to respond, prefers voice notes, likes direct plans.

profile exclusions:

- one-time meeting plans.
- messages sent today.
- links, documents, introductions, or proposals exchanged in a specific interaction.
- promises, tasks, follow-ups, negotiations, conflict, logistics, decisions.
- facts about me unless needed only as relationship context, such as `relationship_to_me`.

## insight rules

write to `newInsights` when the record describes an event, exchange, action, decision, commitment, or topic in the relationship history.

insight examples:

- contact asked, offered, proposed, invited, rejected, agreed, delayed, cancelled, apologized, confirmed.
- i sent, asked, introduced, proposed, reminded, followed up, shared, declined, paid, scheduled.
- we discussed plans, cooperation, conflict, preferences in context, logistics, meeting times, documents, opportunities.
- contact disclosed a stable fact during an interaction; add the stable fact to profile and add an insight only if the disclosure happened in meaningful context.
- pending unresolved loops: waiting for reply, promised intro, owed document, planned meeting, unresolved payment, open decision.

insight exclusions:

- static profile facts with no meaningful interaction.
- repeated fact already present in profile, unless it changes or is contradicted.
- pure greetings, emojis, filler, very short banter with no future value.
- hidden metadata, gaps, timestamps, call markers, media markers as standalone content.

## both destinations

some records create both profile and insight output.

use both when a stable contact fact appears inside a meaningful event.

example:

record: contact says she moved to singapore and asks to meet there next month.

profileUpdates:

```json
{ "base_city": "singapore" }
```

newInsights:

```json
{
  "summary": "对方表示已搬到新加坡，并讨论下个月在当地见面。",
  "category": "生活与近况"
}
```

## contradiction and updates

when new profile fact conflicts with old profile:

- update profile to newest explicit fact.
- create insight describing the change.
- preserve source ids.

example:

old profile: `{ "company": "Tencent" }`

new record: contact says they left Tencent and joined ByteDance.

profileUpdates:

```json
{ "company": "ByteDance" }
```

newInsights:

```json
{
  "summary": "对方表示已离开腾讯并加入字节跳动。",
  "category": "职业与项目"
}
```

## actor rule

insights may mention either actor when it matters.

- mention contact action when contact did something important.
- mention my action when i sent, asked, promised, paid, introduced, shared, or followed up.
- use concise relationship-history phrasing: `我向对方发送...`, `对方邀请...`, `双方约定...`.
- do not erase my role if the useful memory is what i did with the contact.

## category routing

- `职业与项目`: work, projects, companies, professional plans, job changes, business docs.
- `生活与近况`: daily life, health, travel, family, dating, personal updates, meeting logistics.
- `偏好与兴趣`: preferences, tastes, hobbies, recurring likes/dislikes.
- `资源与合作`: intros, resources, deals, cooperation, capital, media, venues, suppliers.
- `随便聊聊`: light but still useful conversational context.
- `忽略`: no lasting value.

## source discipline

- every insight must include `sourceRecordIds`.
- `highlightRecordIds` should include 1 to 3 decisive records.
- never use source ids outside the job payload.
- never infer a profile fact without explicit source support.
- uncertain facts stay in insights, not profile.

## date discipline

- insight date should come from source record `CapturedAt` when present.
- visible timestamp records are context only. use them with `CapturedAt`, not as standalone proof for old backfill.
- if old records lack `CapturedAt`, do not infer historical dates during backfill.

## output decision tree

1. is record hidden metadata or gap only? mark skipped or processed, no profile, no insight.
2. is record meaningless banter? mark skipped, no profile, no insight.
3. does record contain stable fact about contact? add or update `profileUpdates`.
4. did anything happen between me and contact? add `newInsights`.
5. does fact both update contact profile and mark relationship history? output both.
6. does new information contradict old profile or insight? update profile and create insight about change.
7. if still unsure: keep as insight with source, do not write profile.
