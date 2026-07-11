import os
import json
import sys

APP_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PROFILES_DIR = os.path.join(APP_DIR, "profiles")

def apply_consolidation(resolved_json_path):
    if not os.path.exists(resolved_json_path):
        print("No resolved consolidation file found.")
        return

    with open(resolved_json_path, "r", encoding="utf-8") as f:
        merges = json.load(f)

    for contact, update_data in merges.items():
        contact_dir = os.path.join(PROFILES_DIR, contact)
        info_file = os.path.join(contact_dir, "info.json")
        
        if not os.path.exists(info_file):
            continue

        with open(info_file, "r", encoding="utf-8-sig") as f:
            info = json.load(f)

        # 1. Update Profile Fields
        new_profile = update_data.get("updated_profile", {})
        if "profile" not in info:
            info["profile"] = {}
            
        for key, val in new_profile.items():
            if val is not None and val != "":
                info["profile"][key] = val

        # 2. Update Insights
        # The AI returns a complete, revised list of insights (merged or unmodified)
        # We replace the old insights list entirely with this new one
        new_insights = update_data.get("consolidated_insights", [])
        
        # Ensure we drop original_index before saving
        for ins in new_insights:
            ins.pop("original_index", None)
            
        info["insights"] = new_insights
        
        with open(info_file, "w", encoding="utf-8") as f:
            json.dump(info, f, ensure_ascii=False, indent=2)
            
        print(f"Applied consolidation for {contact}.")

if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("Usage: python apply_consolidation.py <resolved_json_path>")
        sys.exit(1)
    apply_consolidation(sys.argv[1])
