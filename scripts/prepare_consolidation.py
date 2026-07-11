import os
import json

APP_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PROFILES_DIR = os.path.join(APP_DIR, "profiles")
TMP_DIR = os.path.join(APP_DIR, "scripts", "insights_tmp")

def prepare_consolidation():
    if not os.path.exists(TMP_DIR):
        os.makedirs(TMP_DIR)

    all_data = {}

    for folder in sorted(os.listdir(PROFILES_DIR)):
        contact_dir = os.path.join(PROFILES_DIR, folder)
        if not os.path.isdir(contact_dir):
            continue

        info_file = os.path.join(contact_dir, "info.json")
        if not os.path.exists(info_file):
            continue

        try:
            with open(info_file, "r", encoding="utf-8-sig") as f:
                info = json.load(f)
        except Exception as e:
            print(f"Error reading {folder}/info.json: {e}")
            continue

        insights = info.get("insights", [])
        if not insights:
            continue

        # we'll send the existing insights and current profile fields
        # if the AI sees demographic facts inside the insights, it should move them to the profile
        current_profile = info.get("profile", {})
        
        # We also need to assign original indices so the AI can return which ones to keep/remove
        indexed_insights = []
        for i, insight in enumerate(insights):
            indexed = dict(insight)
            indexed["original_index"] = i
            indexed_insights.append(indexed)

        all_data[folder] = {
            "current_profile": current_profile,
            "insights": indexed_insights
        }

    out_file = os.path.join(TMP_DIR, "consolidation_queue.json")
    with open(out_file, "w", encoding="utf-8") as f:
        json.dump(all_data, f, ensure_ascii=False, indent=2)
    print(f"Prepared consolidation queue for {len(all_data)} contacts.")

if __name__ == "__main__":
    prepare_consolidation()
