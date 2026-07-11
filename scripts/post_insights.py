import os
import json
import sys

APP_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PROFILES_DIR = os.path.join(APP_DIR, "profiles")

ISLAND_BOUNDARY = "---"
ISLAND_BOUNDARY_TAG = "[ISLAND_BOUNDARY]"
VISIBLE_PENDING_TYPES = {"message", ""}

def normalize(text):
    if not isinstance(text, str):
        return text
    return text.strip().replace('\u2018', "'").replace('\u2019', "'").replace('\u201c', '"').replace('\u201d', '"').replace('\u00a0', '')

def get_text(msg):
    """extract text from a ChatMessage object or legacy string."""
    if isinstance(msg, dict):
        return msg.get("Text") or msg.get("text", "")
    return msg

def get_status(msg):
    """extract status from a ChatMessage object."""
    if isinstance(msg, dict):
        return (msg.get("Status") or msg.get("status", "pending")).lower()
    return "pending"

def get_type(msg):
    if isinstance(msg, dict):
        return (msg.get("Type") or msg.get("type") or "").lower()
    return "message"

def is_hidden_or_gap(msg):
    text = get_text(msg)
    record_type = get_type(msg)
    return text in (ISLAND_BOUNDARY, ISLAND_BOUNDARY_TAG) or record_type not in VISIBLE_PENDING_TYPES

def set_status(msg, status):
    """return a ChatMessage object with updated status."""
    if isinstance(msg, dict):
        updated = dict(msg)
        if "Status" in updated or "status" not in updated:
            updated["Status"] = status
        else:
            updated["status"] = status
        return updated
    # legacy string — convert to object
    return {"Text": msg, "Status": status, "Type": "message"}

def find_message_index(chat_logs, anchor, start_from=0):
    """find the index of an anchor sequence in chat_logs."""
    texts = [get_text(m) for m in chat_logs]
    if isinstance(anchor, list):
        if not anchor: return -1
        for i in range(start_from, len(texts) - len(anchor) + 1):
            match = all(normalize(texts[i+j]) == normalize(anchor[j]) for j in range(len(anchor)))
            if match:
                return i
        return -1
    else:
        for i in range(start_from, len(texts)):
            if normalize(texts[i]) == normalize(anchor):
                return i
        return -1

def save_insights(contact_folder, insights_file_path):
    contact_dir = os.path.join(PROFILES_DIR, contact_folder)
    chat_file = os.path.join(contact_dir, "chat_history.json")
    info_file = os.path.join(contact_dir, "info.json")

    if not os.path.exists(contact_dir) or not os.path.exists(info_file):
        print(f"contact {contact_folder} not found or missing info.json.")
        return

    if not os.path.exists(insights_file_path):
        print(f"insights file {insights_file_path} not found.")
        return

    try:
        with open(insights_file_path, "r", encoding="utf-8-sig") as f:
            new_insights = json.load(f)
        if not isinstance(new_insights, list):
            new_insights = []

        valid_categories = {"职业与项目", "生活与近况", "偏好与兴趣", "资源与合作", "随便聊聊", "忽略"}
        for insight in new_insights:
            if insight.get("category") not in valid_categories:
                insight["category"] = "随便聊聊"

    except json.JSONDecodeError:
        print("invalid json string provided.")
        return

    with open(chat_file, "r", encoding="utf-8-sig") as f:
        try:
            chat_logs = json.load(f)
        except json.JSONDecodeError:
            chat_logs = []

    with open(info_file, "r", encoding="utf-8-sig") as f:
        try:
            info = json.load(f)
        except json.JSONDecodeError:
            return

    # determine which message indices are covered by the new insights
    covered_indices = set()
    for insight in new_insights:
        start_anchor = insight.get("start")
        end_anchor = insight.get("end")
        if start_anchor and end_anchor:
            idx1 = find_message_index(chat_logs, start_anchor)
            idx2 = find_message_index(chat_logs, end_anchor)
            if idx1 >= 0 and idx2 >= 0:
                s = min(idx1, idx2)
                e = max(idx1, idx2)
                last_seq = end_anchor if e == idx2 else start_anchor
                if isinstance(last_seq, list):
                    e += len(last_seq) - 1
                for i in range(s, e + 1):
                    covered_indices.add(i)

    # if no anchors resolved, fall back to covering all pending messages
    if not covered_indices:
        for i, msg in enumerate(chat_logs):
            if get_status(msg) == "pending" and not is_hidden_or_gap(msg):
                covered_indices.add(i)

    # update statuses in chat_logs
    updated_logs = []
    for i, msg in enumerate(chat_logs):
        if i in covered_indices:
            # determine status: skipped for ignored insights, processed otherwise
            is_ignored = any(
                ins.get("category") == "忽略" for ins in new_insights
            )
            new_status = "skipped" if is_ignored else "processed"
            updated_logs.append(set_status(msg, new_status))
        elif get_status(msg) == "pending" and is_hidden_or_gap(msg):
            updated_logs.append(set_status(msg, "processed"))
        else:
            updated_logs.append(msg if isinstance(msg, dict) else {"Text": msg, "Status": get_status(msg), "Type": "message"})

    with open(chat_file, "w", encoding="utf-8-sig") as f:
        json.dump(updated_logs, f, ensure_ascii=False, indent=2)

    # save insights (skip ignored ones from going into info.json, they are just for queue clearing)
    if new_insights:
        existing_insights = info.get("insights", [])
        seen = {x["summary"] for x in existing_insights if "summary" in x}
        added_count = 0
        for ni in new_insights:
            if ni.get("category") == "忽略":
                continue  # don't persist ignored insights in the timeline
            if ni.get("summary") not in seen:
                # use per-insight anchors from AI output; fall back to all covered
                if not ni.get("start") or not ni.get("end"):
                    real_msgs = [get_text(m) for m in chat_logs if not is_hidden_or_gap(m)]
                    if real_msgs:
                        ni["start"] = real_msgs[0:3]
                        ni["end"] = real_msgs[-3:] if len(real_msgs) >= 3 else real_msgs
                existing_insights.append(ni)
                seen.add(ni.get("summary"))
                added_count += 1
        info["insights"] = existing_insights
        print(f"saved {added_count} new insights for {contact_folder}.")

    # remove legacy fields
    info.pop("firstindex", None)
    info.pop("lastindex", None)
    info.pop("FirstIndex", None)
    info.pop("LastIndex", None)
    info.pop("analyzedsegments", None)

    with open(info_file, "w", encoding="utf-8") as f:
        json.dump(info, f, ensure_ascii=False, indent=2)

    # clean legacy files
    insights_file = os.path.join(contact_dir, "insights.json")
    state_file = os.path.join(contact_dir, "insights_state.json")
    try:
        if os.path.exists(insights_file): os.remove(insights_file)
        if os.path.exists(state_file): os.remove(state_file)
    except:
        pass

if __name__ == "__main__":
    if len(sys.argv) < 3:
        print("Usage: python post_insights.py <contact_folder> <json_file_path>")
        sys.exit(1)

    contact = sys.argv[1]
    json_file = sys.argv[2]
    save_insights(contact, json_file)
