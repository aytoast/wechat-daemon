import os
import json
import sys

APP_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PROFILES_DIR = os.path.join(APP_DIR, "profiles")
TMP_DIR = os.path.join(APP_DIR, "scripts", "insights_tmp")

def apply_merges(resolved_json_path):
    if not os.path.exists(resolved_json_path):
        print("No resolved merges file found.")
        return

    with open(resolved_json_path, "r", encoding="utf-8") as f:
        merges = json.load(f)

    for contact, contact_merges in merges.items():
        contact_dir = os.path.join(PROFILES_DIR, contact)
        info_file = os.path.join(contact_dir, "info.json")
        chat_file = os.path.join(contact_dir, "chat_history.json")
        
        if not os.path.exists(info_file):
            continue

        with open(info_file, "r", encoding="utf-8-sig") as f:
            info = json.load(f)

        insights = info.get("insights", [])
        if not insights:
            continue
            
        with open(chat_file, "r", encoding="utf-8-sig") as f:
            chat_logs = json.load(f)

        # Sort merges in reverse order by original_indices so we can delete safely
        # But we need to be careful: multiple merges could affect different original indices.
        # It's safer to build a new insights list.
        
        indices_to_remove = set()
        new_insights_to_add = []
        
        for merge_result in contact_merges:
            indices = merge_result.get("original_indices", [])
            for idx in indices:
                indices_to_remove.add(idx)
            
            # The AI generates a new summary and category
            merged_insight = {
                "summary": merge_result["summary"],
                "category": merge_result["category"]
            }
            if merge_result.get("date"):
                merged_insight["date"] = merge_result["date"]
                
            bounds = merge_result.get("cluster_bounds", [])
            if len(bounds) == 2:
                # Generate new start and end anchors from the chat logs based on the bounds
                min_start = bounds[0]
                max_end = bounds[1]
                
                # Fetch up to 3 messages for start anchor, skipping ISLAND_BOUNDARY
                start_anchor = []
                for i in range(min_start, min(min_start + 5, len(chat_logs))):
                    text = chat_logs[i].get("Text") if isinstance(chat_logs[i], dict) else chat_logs[i]
                    if text != "---" and text != "[ISLAND_BOUNDARY]":
                        start_anchor.append(text)
                        if len(start_anchor) == 3: break
                
                end_anchor = []
                for i in range(max_end, max(max_end - 5, -1), -1):
                    text = chat_logs[i].get("Text") if isinstance(chat_logs[i], dict) else chat_logs[i]
                    if text != "---" and text != "[ISLAND_BOUNDARY]":
                        end_anchor.insert(0, text)
                        if len(end_anchor) == 3: break
                        
                merged_insight["start"] = start_anchor
                merged_insight["end"] = end_anchor
                
            new_insights_to_add.append(merged_insight)
            
        new_insights_list = []
        for i, insight in enumerate(insights):
            if i not in indices_to_remove:
                new_insights_list.append(insight)
                
        new_insights_list.extend(new_insights_to_add)
        info["insights"] = new_insights_list
        
        with open(info_file, "w", encoding="utf-8") as f:
            json.dump(info, f, ensure_ascii=False, indent=2)
            
        print(f"Applied merges for {contact}. Replaced {len(indices_to_remove)} old insights with {len(new_insights_to_add)} merged insights.")

if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("Usage: python apply_merges.py <resolved_json_path>")
        sys.exit(1)
    apply_merges(sys.argv[1])
