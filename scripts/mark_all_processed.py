"""
mark_all_processed.py
Sweeps through all profiles and marks every message in chat_history.json as 'processed'.
Run this once to get a clean slate before the new status-tracking system takes over.
"""
import os
import json

APP_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PROFILES_DIR = os.path.join(APP_DIR, "profiles")

def get_text(msg):
    if isinstance(msg, dict):
        return msg.get("Text") or msg.get("text", "")
    return msg

def run():
    if not os.path.exists(PROFILES_DIR):
        print("profiles directory not found.")
        return

    total_contacts = 0
    total_messages = 0

    for folder in sorted(os.listdir(PROFILES_DIR)):
        contact_dir = os.path.join(PROFILES_DIR, folder)
        if not os.path.isdir(contact_dir):
            continue

        chat_file = os.path.join(contact_dir, "chat_history.json")
        if not os.path.exists(chat_file):
            continue

        try:
            with open(chat_file, "r", encoding="utf-8-sig") as f:
                chat_logs = json.load(f)
        except:
            print(f"  skipping {folder} (unreadable)")
            continue

        updated = []
        count = 0
        for msg in chat_logs:
            text = get_text(msg)
            updated.append({"Text": text, "Status": "processed"})
            count += 1

        with open(chat_file, "w", encoding="utf-8-sig") as f:
            json.dump(updated, f, ensure_ascii=False, indent=2)

        print(f"  {folder}: {count} messages marked as processed")
        total_contacts += 1
        total_messages += count

    print(f"\ndone. {total_messages} messages across {total_contacts} contacts marked as processed.")

if __name__ == "__main__":
    run()
