import os
import json
import sys

APP_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PROFILES_DIR = os.path.join(APP_DIR, "profiles")

def get_insights(contact_folder):
    contact_dir = os.path.join(PROFILES_DIR, contact_folder)
    info_file = os.path.join(contact_dir, "info.json")

    if not os.path.exists(info_file):
        print(f"no info.json found for {contact_folder}.")
        return

    try:
        with open(info_file, "r", encoding="utf-8-sig") as f:
            info = json.load(f)
    except json.JSONDecodeError:
        print("invalid info.json")
        return

    insights = info.get("insights", [])
    if not insights:
        print(f"no existing insights for {contact_folder}.")
        return
        
    print(json.dumps(insights, ensure_ascii=False, indent=2))

if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("Usage: python get_insights.py <contact_folder>")
        sys.exit(1)
        
    contact = sys.argv[1]
    get_insights(contact)
