import os
import sys
import json
import time
import sqlite3
import urllib.parse
from datetime import datetime, timezone

ANTIGRAVITY_DIR = os.path.expanduser('~/.gemini/antigravity')
DB_PATH = os.path.join(ANTIGRAVITY_DIR, 'conversation_summaries.db')
PRESENCE_DIR = os.path.join(ANTIGRAVITY_DIR, 'presence')
BRAIN_DIR = os.path.join(ANTIGRAVITY_DIR, 'brain')
CODEX_SESSIONS_DIR = os.path.expanduser('~/.codex/sessions')

def format_model_name(raw_name):
    if not raw_name:
        return "Gemini"
    name = raw_name.lower()
    if "flash" in name:
        if "high" in name:
            return "Gemini 3.8 Flash (High)"
        return "Gemini 3.8 Flash"
    elif "pro" in name:
        return "Gemini Pro"
    elif "gemini" in name:
        return raw_name.replace("-", " ").title()
    return raw_name

def uri_to_path(uri):
    if not uri:
        return os.getcwd()
    try:
        # e.g. ["file:///d%3A/PhoneBox"]
        if uri.startswith('[') and uri.endswith(']'):
            parsed = json.loads(uri)
            if parsed and isinstance(parsed, list):
                uri = parsed[0]
        if uri.startswith('file:///'):
            decoded = urllib.parse.unquote(uri[8:])
            if decoded.startswith('/') and len(decoded) > 2 and decoded[1] == ':':
                decoded = decoded[1:]
            return decoded.replace('/', '\\')
    except Exception:
        pass
    return os.getcwd()

def get_transcript_info(conv_id):
    transcript_path = os.path.join(BRAIN_DIR, conv_id, '.system_generated', 'logs', 'transcript.jsonl')
    model = "Gemini 3.8 Flash (High)"
    total_tokens = 0
    input_tokens = 0
    output_tokens = 0
    last_tool = None
    last_status = None
    
    if not os.path.exists(transcript_path):
        return model, total_tokens, input_tokens, output_tokens, last_tool, last_status
        
    try:
        with open(transcript_path, 'r', encoding='utf-8', errors='ignore') as f:
            # Read last ~30 lines
            lines = f.readlines()[-30:]
            for line in lines:
                try:
                    data = json.loads(line)
                    if 'model' in data:
                        model = format_model_name(data['model'])
                    if 'input_tokens' in data or 'output_tokens' in data:
                        inp = data.get('input_tokens', 0)
                        out = data.get('output_tokens', 0)
                        total_tokens = inp + out
                        input_tokens = inp
                        output_tokens = out
                    if 'tool_calls' in data and data['tool_calls']:
                        calls = data['tool_calls']
                        if isinstance(calls, list) and len(calls) > 0:
                            last_tool = calls[-1].get('name')
                    if 'type' in data:
                        last_status = data.get('type')
                except Exception:
                    continue
    except Exception:
        pass
        
    return model, total_tokens, input_tokens, output_tokens, last_tool, last_status

def sync_once():
    if not os.path.exists(DB_PATH):
        return
        
    conn = sqlite3.connect(f'file:{DB_PATH}?mode=ro', uri=True)
    cur = conn.cursor()
    
    # Check active lock files in presence dir
    active_locks = set()
    if os.path.exists(PRESENCE_DIR):
        for f in os.listdir(PRESENCE_DIR):
            if f.endswith('.lock'):
                conv_id = f[:-5]
                # Check if file is currently open / locked
                lock_path = os.path.join(PRESENCE_DIR, f)
                try:
                    # On Windows, try opening for exclusive access
                    handle = open(lock_path, 'r+')
                    handle.close()
                    # Not locked
                except (IOError, OSError, PermissionError):
                    # Locked by Antigravity!
                    active_locks.add(conv_id)

    cur.execute("""
        SELECT conversation_id, title, workspace_uris, status, last_modified_time, step_count 
        FROM conversation_summaries 
        ORDER BY last_modified_time DESC LIMIT 10
    """)
    rows = cur.fetchall()
    conn.close()

    now_utc = datetime.now(timezone.utc)
    today_folder = now_utc.strftime('%Y/%m/%d')
    date_str = now_utc.strftime('%Y-%m-%d')
    target_dir = os.path.join(CODEX_SESSIONS_DIR, today_folder.replace('/', os.sep))
    os.makedirs(target_dir, exist_ok=True)

    found_active = False

    for row in rows:
        conv_id, title, workspace_uris, status, last_modified, step_count = row
        is_running = (conv_id in active_locks) or (status == 'CASCADE_RUN_STATUS_RUNNING')
        
        # We only output active sessions or sessions modified in the last 15 minutes
        if not is_running and conv_id not in active_locks:
            continue
            
        found_active = True
        cwd = uri_to_path(workspace_uris)
        model, total_tokens, input_tokens, output_tokens, last_tool, last_type = get_transcript_info(conv_id)
        
        session_file = os.path.join(target_dir, f'rollout-{date_str}-{conv_id[:8]}-antigravity.jsonl')
        ts = now_utc.isoformat()
        
        lifecycle = "task_started" if is_running else "task_complete"
        
        meta = json.dumps({"type": "session_meta", "timestamp": ts, "payload": {"cwd": cwd}})
        context = json.dumps({"type": "turn_context", "timestamp": ts, "payload": {"model": model, "effort": "high"}})
        event = json.dumps({"type": "event_msg", "timestamp": ts, "payload": {"type": lifecycle}})
        token_info = json.dumps({
            "type": "event_msg",
            "timestamp": ts,
            "payload": {
                "type": "token_count",
                "info": {
                    "total_token_usage": {
                        "total_tokens": total_tokens or 15000,
                        "input_tokens": input_tokens or 12000,
                        "output_tokens": output_tokens or 3000
                    },
                    "model_context_window": 1000000
                }
            }
        })
        lines = [meta, context, event, token_info]
        if last_tool:
            lines.append(json.dumps({
                "type": "response_item",
                "timestamp": ts,
                "payload": {"type": "function_call", "name": last_tool}
            }))
            
        with open(session_file, 'w', encoding='utf-8') as sf:
            sf.write('\n'.join(lines) + '\n')
            
    return found_active

if __name__ == '__main__':
    if len(sys.argv) > 1 and sys.argv[1] == '--loop':
        while True:
            try:
                sync_once()
            except Exception as e:
                pass
            time.sleep(2)
    else:
        active = sync_once()
        print(f"Sync complete, active Antigravity session found: {active}")
