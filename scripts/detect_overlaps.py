import os
import json

APP_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PROFILES_DIR = os.path.join(APP_DIR, "profiles")
TMP_DIR = os.path.join(APP_DIR, "scripts", "insights_tmp")
ISLAND_BOUNDARY = "---"

def normalize(text):
    if not isinstance(text, str):
        return text
    return text.strip().replace('\u2018', "'").replace('\u2019', "'").replace('\u201c', '"').replace('\u201d', '"').replace('\u00a0', '')

def get_text(msg):
    if isinstance(msg, dict):
        return msg.get("Text") or msg.get("text", "")
    return msg

def find_message_index(chat_logs, anchor, start_from=0):
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

def detect_overlaps():
    if not os.path.exists(TMP_DIR):
        os.makedirs(TMP_DIR)

    all_clusters = {}

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

        insights = info.get("insights", [])
        if not insights:
            continue

        # Map each insight to its start and end index
        indexed_insights = []
        for i, insight in enumerate(insights):
            start_anchor = insight.get("start")
            end_anchor = insight.get("end")
            idx1 = find_message_index(chat_logs, start_anchor) if start_anchor else -1
            idx2 = find_message_index(chat_logs, end_anchor) if end_anchor else -1

            if idx1 >= 0 and idx2 >= 0:
                s = min(idx1, idx2)
                e = max(idx1, idx2)
                last_seq = end_anchor if e == idx2 else start_anchor
                if isinstance(last_seq, list):
                    e += len(last_seq) - 1
                
                # Filter out single meaningless messages or very short overlaps if needed, 
                # but mathematically we just record the bounds.
                indexed_insights.append({
                    "original_index": i,
                    "insight": insight,
                    "start_idx": s,
                    "end_idx": e
                })
            else:
                # If anchors are missing or unresolvable, we leave it alone (cannot cluster).
                pass

        if not indexed_insights:
            continue

        # Sort by start_idx
        indexed_insights.sort(key=lambda x: x["start_idx"])

        clusters = []
        current_cluster = [indexed_insights[0]]

        for current in indexed_insights[1:]:
            prev = current_cluster[-1]
            # Overlap condition: current starts before previous ends 
            # (or exactly when it ends, sharing a message)
            if current["start_idx"] <= prev["end_idx"]:
                current_cluster.append(current)
                # Update the logical end of the cluster to the maximum end seen so far
                # Actually, we don't need to mutate prev, just check the MAX end of the entire cluster.
                cluster_max_end = max(item["end_idx"] for item in current_cluster)
                # Wait, if current["start_idx"] <= cluster_max_end, it belongs to the cluster!
            else:
                cluster_max_end = max(item["end_idx"] for item in current_cluster)
                if current["start_idx"] <= cluster_max_end:
                     current_cluster.append(current)
                else:
                     clusters.append(current_cluster)
                     current_cluster = [current]
        
        clusters.append(current_cluster)

        # Filter clusters to only those that have > 1 insight
        valid_clusters = [c for c in clusters if len(c) > 1]

        if valid_clusters:
            # We want to output the context text for each cluster so the AI can merge it
            contact_clusters_out = []
            for c in valid_clusters:
                min_start = min(item["start_idx"] for item in c)
                max_end = max(item["end_idx"] for item in c)
                
                # Get the raw text of the entire cluster's span
                raw_messages = [get_text(chat_logs[i]) for i in range(min_start, max_end + 1) if get_text(chat_logs[i]) != ISLAND_BOUNDARY]

                contact_clusters_out.append({
                    "cluster_bounds": [min_start, max_end],
                    "raw_conversation": raw_messages,
                    "overlapping_insights": [
                        {
                            "summary": item["insight"].get("summary"),
                            "category": item["insight"].get("category"),
                            "date": item["insight"].get("date"),
                            "original_index": item["original_index"]
                        } for item in c
                    ]
                })
            
            all_clusters[folder] = contact_clusters_out

    if all_clusters:
        out_file = os.path.join(TMP_DIR, "merge_candidates.json")
        with open(out_file, "w", encoding="utf-8") as f:
            json.dump(all_clusters, f, ensure_ascii=False, indent=2)
        print("Found overlapping insights. Candidates written to merge_candidates.json")
    else:
        print("No overlapping insights detected.")

if __name__ == "__main__":
    detect_overlaps()
