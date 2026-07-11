import os
import json

APP_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PROFILES_DIR = os.path.join(APP_DIR, "profiles")

ISLAND_BOUNDARY = "---"

def find_message_index(chat_logs, anchor, start_from=0):
    if isinstance(anchor, list):
        if not anchor: return -1
        for i in range(start_from, len(chat_logs) - len(anchor) + 1):
            match = True
            for j, msg in enumerate(anchor):
                if chat_logs[i+j] != msg:
                    match = False
                    break
            if match:
                return i
        return -1
    else:
        for i in range(start_from, len(chat_logs)):
            if chat_logs[i] == anchor:
                return i
        return -1

def backfill_insight_anchors():
    if not os.path.exists(PROFILES_DIR):
        print("profiles directory not found.")
        return

    for folder in sorted(os.listdir(PROFILES_DIR)):
        contact_dir = os.path.join(PROFILES_DIR, folder)
        if not os.path.isdir(contact_dir):
            continue

        chat_file = os.path.join(contact_dir, "chat_history.json")
        info_file = os.path.join(contact_dir, "info.json")

        if not os.path.exists(chat_file) or not os.path.exists(info_file):
            continue

        try:
            with open(chat_file, "r", encoding="utf-8-sig") as f:
                chat_logs = json.load(f)
            with open(info_file, "r", encoding="utf-8-sig") as f:
                info = json.load(f)
        except Exception as e:
            print(f"[{folder}] skip — {e}")
            continue

        insights = info.get("insights", [])
        segments = info.get("analyzedsegments", [])

        # build a default fallback from the first available segment
        fallback_seg = segments[0] if segments else None

        # also build a global fallback from real messages if no segments
        if not fallback_seg:
            real_msgs = [m for m in chat_logs if m != ISLAND_BOUNDARY]
            if real_msgs:
                fallback_seg = {
                    "start": real_msgs[0:3],
                    "end": real_msgs[-3:] if len(real_msgs) >= 3 else real_msgs
                }

        patched = 0
        for insight in insights:
            if not insight.get("start") or not insight.get("end"):
                # try to find the best matching segment by date
                best_seg = None
                insight_date = insight.get("date", "")

                for seg in segments:
                    s = find_message_index(chat_logs, seg["start"])
                    e = find_message_index(chat_logs, seg["end"])
                    if s < 0 or e < 0:
                        continue
                    start_idx = min(s, e)
                    end_idx = max(s, e)
                    if isinstance(seg["end"], list):
                        end_idx += len(seg["end"]) - 1

                    # check if any message near this segment contains the insight's date
                    found_date = False
                    for i in range(start_idx, min(end_idx + 1, len(chat_logs))):
                        if insight_date and insight_date in chat_logs[i]:
                            found_date = True
                            break
                    if found_date:
                        best_seg = seg
                        break

                chosen = best_seg if best_seg else fallback_seg
                if chosen:
                    insight["start"] = chosen["start"]
                    insight["end"] = chosen["end"]
                    patched += 1

        if patched > 0:
            info["insights"] = insights
            with open(info_file, "w", encoding="utf-8-sig") as f:
                json.dump(info, f, ensure_ascii=False, indent=2)
            print(f"[{folder}] patched {patched} insight(s)")
        else:
            print(f"[{folder}] no changes needed")

if __name__ == "__main__":
    backfill_insight_anchors()
