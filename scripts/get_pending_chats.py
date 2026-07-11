import os
import json

APP_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PROFILES_DIR = os.path.join(APP_DIR, "profiles")

ISLAND_BOUNDARY = "---"
ISLAND_BOUNDARY_TAG = "[ISLAND_BOUNDARY]"
VISIBLE_PENDING_TYPES = {"message", ""}

def get_text(msg):
    """extract text from a ChatMessage object or legacy string."""
    if isinstance(msg, dict):
        return msg.get("Text") or msg.get("text", "")
    return msg

def get_status(msg):
    """extract status from a ChatMessage object, default to 'pending' for legacy strings."""
    if isinstance(msg, dict):
        return (msg.get("Status") or msg.get("status", "pending")).lower()
    return "pending"

def get_type(msg):
    if isinstance(msg, dict):
        return (msg.get("Type") or msg.get("type") or "").lower()
    return "message"

def set_status(msg, status):
    if isinstance(msg, dict):
        updated = dict(msg)
        if "Status" in updated or "status" not in updated:
            updated["Status"] = status
        else:
            updated["status"] = status
        return updated
    return {"Text": msg, "Status": status, "Type": "message"}

def is_hidden_or_gap(msg):
    text = get_text(msg)
    record_type = get_type(msg)
    return text in (ISLAND_BOUNDARY, ISLAND_BOUNDARY_TAG) or record_type not in VISIBLE_PENDING_TYPES

def get_pending():
    if not os.path.exists(PROFILES_DIR):
        print("profiles directory not found.")
        return

    has_pending = False
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
        except:
            continue

        pending = []
        changed = False
        normalized_logs = []
        for msg in chat_logs:
            text = get_text(msg)
            status = get_status(msg)
            if status == "pending" and is_hidden_or_gap(msg):
                normalized_logs.append(set_status(msg, "processed"))
                changed = True
                continue
            normalized_logs.append(msg)
            if status == "pending" and text and not is_hidden_or_gap(msg):
                pending.append(text)

        if changed:
            with open(chat_file, "w", encoding="utf-8") as f:
                json.dump(normalized_logs, f, ensure_ascii=False, indent=2)

        if pending:
            has_pending = True
            print(f"=== CONTACT: {folder} ===")

            insights_list = info.get("insights", [])
            if insights_list:
                print("[Recent Insights Context]:")
                for ins in insights_list[-3:]:
                    print(f"- [{ins.get('category', 'Unknown')}]: {ins.get('summary', '')}")
                print("\n[Pending Messages]:")

            for msg in pending:
                print(msg)
            print("==========================\n")

    if not has_pending:
        print("no pending chats to analyze.")

if __name__ == "__main__":
    get_pending()
