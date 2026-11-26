"""Giant Creature Arena CLI & Automated Versus Video Recorder.

Supports three workflows for external agents and creators:
  1. direct launch: Launch RimWorld straight into a preset arena map with two mutually
     hostile giants via `-fast -quicktest -gcfarena=...` (no GABS required).
  2. gabs launch:   Start GABS + RimWorld, call `gcf_arena/setup_match`, verify both
     hostile giants are spawned and framed, and print live connection details.
  3. record:        End-to-end automated giant-vs-giant battle video recorder via GABS +
     RimBridgeServer (`gcf_arena/*`), producing H.264 MP4, contact sheet, and telemetry JSON.
"""

from __future__ import annotations

import argparse
import http.client
import io
import json
import os
import pathlib
import re
import shutil
import subprocess
import sys
import time
import xml.etree.ElementTree as ET
from PIL import Image, ImageDraw, ImageFont

MOD_ROOT = pathlib.Path(__file__).resolve().parent.parent
VERIFY_ROOT = MOD_ROOT / ".verify"
DEFAULT_RIMWORLD_DIR = VERIFY_ROOT / "isolated_rw_host"
DEFAULT_RIMWORLD_EXE = DEFAULT_RIMWORLD_DIR / "RimWorldWin64.exe"
DEFAULT_RUNTIME_DIR = VERIFY_ROOT / "arena_runtime"
DEFAULT_GABS_DIR = VERIFY_ROOT / "gabs_arena"
USER_RW_ROOT = pathlib.Path.home() / "AppData/LocalLow/Ludeon Studios/RimWorld by Ludeon Studios"


def resolve_executable(explicit_value: str, env_var: str, command_names: tuple[str, ...]) -> pathlib.Path | None:
    if explicit_value:
        candidate = pathlib.Path(explicit_value).expanduser()
        if candidate.is_file():
            return candidate.resolve()
        found = shutil.which(explicit_value)
        if found:
            return pathlib.Path(found).resolve()
    env_val = os.environ.get(env_var, "").strip()
    if env_val:
        candidate = pathlib.Path(env_val).expanduser()
        if candidate.is_file():
            return candidate.resolve()
        found = shutil.which(env_val)
        if found:
            return pathlib.Path(found).resolve()
    for name in command_names:
        found = shutil.which(name)
        if found:
            return pathlib.Path(found).resolve()
    return None


def resolve_gabs_exe(explicit_value: str) -> pathlib.Path:
    resolved = resolve_executable(explicit_value, "GABS_EXE", ("gabs.exe", "gabs"))
    if resolved is None:
        raise FileNotFoundError("gabs executable not found. Pass --gabs-exe, set GABS_EXE, or add gabs to PATH.")
    return resolved


def resolve_ffmpeg_exe(explicit_value: str) -> pathlib.Path | None:
    return resolve_executable(explicit_value, "FFMPEG_EXE", ("ffmpeg.exe", "ffmpeg"))

ARENA_MODS = [
    "brrainz.harmony",
    "ludeon.rimworld",
    "ludeon.rimworld.royalty",
    "ludeon.rimworld.ideology",
    "ludeon.rimworld.biotech",
    "ludeon.rimworld.anomaly",
    "ludeon.rimworld.odyssey",
    "brrainz.rimbridgeserver",
    "sanguo.giantcreatureframework",
]

KNOWN_EXPANSIONS = [
    "ludeon.rimworld.royalty",
    "ludeon.rimworld.ideology",
    "ludeon.rimworld.biotech",
    "ludeon.rimworld.anomaly",
    "ludeon.rimworld.odyssey",
]

BUILT_IN_PRESETS = [
    ("Colosseum", "Paved sandstone/granite colosseum with center clash ring and corner pillars."),
    ("Plains", "Open flat grassland battlefield with unobstructed sightlines."),
    ("DesertRuins", "Sun-baked sand arena with symmetric off-center crushable sandstone ruins."),
    ("Tundra", "High-contrast marble and limestone frost arena without movement penalties."),
    ("VolcanicBasalt", "Dark slate caldera floor designed to make glowing VFX pop on camera."),
]


def extract_attention_ids(obj) -> list[str]:
    found: list[str] = []
    if isinstance(obj, dict):
        for k, v in obj.items():
            kl = str(k).lower()
            if kl in ("attentionid", "attention_id") and isinstance(v, str) and v.strip():
                found.append(v.strip())
            elif kl == "id" and isinstance(v, str) and ("att" in v.lower() or len(v) >= 4) and not v.startswith("rimworld-"):
                found.append(v.strip())
            else:
                found.extend(extract_attention_ids(v))
    elif isinstance(obj, list):
        for item in obj:
            found.extend(extract_attention_ids(item))
    elif isinstance(obj, str):
        for m in re.finditer(r"(?:attention[\s_-]*id|attentionId|attention\s+item)[\"'=:\s]+([A-Za-z0-9._:-]+)", obj, re.IGNORECASE):
            found.append(m.group(1))
        for m in re.finditer(r"\b(attn?[-_][A-Za-z0-9._:-]+)\b", obj, re.IGNORECASE):
            found.append(m.group(1))
    return [x for x in dict.fromkeys(found) if not x.startswith("rimworld-")]


class GabsClient:
    def __init__(self, host: str = "127.0.0.1", port: int = 8104, game_id: str = "rimworld-giant-arena"):
        self.host = host
        self.port = port
        self.game_id = game_id
        self.conn = http.client.HTTPConnection(host, port, timeout=180)
        self.req_id = 0

    def rpc(self, method: str, params: dict | None = None, timeout: int = 180):
        self.req_id += 1
        payload = json.dumps({
            "jsonrpc": "2.0",
            "id": self.req_id,
            "method": method,
            "params": params or {},
        }).encode("utf-8")
        headers = {"Content-Type": "application/json", "Connection": "keep-alive"}
        try:
            self.conn.timeout = timeout
            self.conn.request("POST", "/mcp", payload, headers)
            resp = self.conn.getresponse()
            raw = resp.read()
        except Exception:
            self.conn.close()
            self.conn = http.client.HTTPConnection(self.host, self.port, timeout=timeout)
            self.conn.timeout = timeout
            self.conn.request("POST", "/mcp", payload, headers)
            resp = self.conn.getresponse()
            raw = resp.read()
        data = json.loads(raw)
        if "error" in data and data["error"]:
            raise RuntimeError(f"RPC error on {method}: {data['error']}")
        return data.get("result", {})

    def call_mcp(self, tool_name: str, arguments: dict, timeout: int = 180):
        if tool_name != "games_call_tool":
            self.conn.close()
            self.conn = http.client.HTTPConnection(self.host, self.port, timeout=timeout)
        res_obj = self.rpc("tools/call", {"name": tool_name, "arguments": arguments}, timeout=timeout)
        out = []
        for item in (res_obj.get("content", []) if isinstance(res_obj, dict) else []):
            if item.get("type") == "text":
                try:
                    out.append(json.loads(item["text"]))
                except Exception:
                    out.append(item["text"])
            else:
                out.append(item)
        if isinstance(res_obj, dict) and res_obj.get("isError"):
            raise RuntimeError(f"MCP tool error on {tool_name}: {out[0] if len(out) == 1 else out}")
        return out[0] if len(out) == 1 else out

    def drain_attention_if_any(self, hint_msg: str = ""):
        seen: set[str] = set()
        if hint_msg:
            for hid in extract_attention_ids(hint_msg):
                try:
                    self.call_mcp("games_ack_attention", {"gameId": self.game_id, "attentionId": hid, "timeout": 10}, timeout=15)
                    seen.add(hid)
                except Exception:
                    pass
        for _ in range(15):
            try:
                att = self.call_mcp("games_get_attention", {"gameId": self.game_id, "timeout": 10}, timeout=15)
                ids = [aid for aid in extract_attention_ids(att) if aid not in seen]
                if not ids:
                    break
                progressed = False
                for aid in ids:
                    seen.add(aid)
                    try:
                        self.call_mcp("games_ack_attention", {"gameId": self.game_id, "attentionId": aid, "timeout": 10}, timeout=15)
                        progressed = True
                    except Exception:
                        pass
                if not progressed:
                    break
            except Exception:
                break

    def rw(self, tool: str, args: dict | None = None, timeout: int = 120):
        norm = tool.replace("/", "_")
        prefix = f"{self.game_id}_"
        full = norm if norm.startswith(prefix) else f"{prefix}{norm}"
        for _ in range(5):
            try:
                return self.call_mcp("games_call_tool", {"tool": full, "arguments": args or {}}, timeout=timeout)
            except RuntimeError as ex:
                msg = str(ex)
                if "requires acknowledgement" in msg or "games_ack_attention" in msg:
                    self.drain_attention_if_any(msg)
                    continue
                raise
        return self.call_mcp("games_call_tool", {"tool": full, "arguments": args or {}}, timeout=timeout)


def prepare_runtime_dir(
    runtime_dir: pathlib.Path,
    gabs_dir: pathlib.Path,
    rimworld_exe: pathlib.Path,
    game_id: str,
    width: int,
    height: int,
    extra_launch_args: list[str] | None = None,
    lang: str | None = None,
    mods_config_path: pathlib.Path | None = None,
):
    runtime_dir.mkdir(parents=True, exist_ok=True)
    (runtime_dir / "Screenshots").mkdir(parents=True, exist_ok=True)
    cfg_dir = runtime_dir / "Config"
    cfg_dir.mkdir(parents=True, exist_ok=True)

    user_cfg = USER_RW_ROOT / "Config"
    if mods_config_path is not None and user_cfg.exists():
        # Copy user Mod_*.xml settings so large multi-race modpacks retain existing settings & skip first-run popups
        for f in user_cfg.glob("Mod_*.xml"):
            dst = cfg_dir / f.name
            if not dst.exists() or f.stat().st_mtime > dst.stat().st_mtime:
                shutil.copy2(f, dst)
        src_creatures = user_cfg / "GiantCreatureFramework" / "Creatures"
        if src_creatures.exists():
            dst_creatures = cfg_dir / "GiantCreatureFramework" / "Creatures"
            if dst_creatures.exists():
                shutil.rmtree(dst_creatures)
            shutil.copytree(src_creatures, dst_creatures)

    src_prefs = user_cfg / "Prefs.xml"
    root = ET.fromstring(src_prefs.read_text(encoding="utf-8-sig")) if src_prefs.exists() else ET.Element("PrefsData")
    for bad in ("vSyncCount", "targetFrameRate"):
        b_el = root.find(bad)
        if b_el is not None:
            root.remove(b_el)
    overrides = {
        "devMode": "True",
        "runInBackground": "True",
        "pauseOnLoad": "False",
        "pauseOnError": "False",
        "automaticPauseMode": "Never",
        "animalNameMode": "None",
        "screenWidth": str(width),
        "screenHeight": str(height),
        "fullscreen": "False",
    }
    if lang:
        overrides["langFolderName"] = lang
    for k, v in overrides.items():
        el = root.find(k)
        if el is None:
            el = ET.SubElement(root, k)
        el.text = v
    (cfg_dir / "Prefs.xml").write_bytes(ET.tostring(root, encoding="utf-8", xml_declaration=True))

    if mods_config_path is not None:
        if not mods_config_path.exists():
            raise FileNotFoundError(f"ModsConfig file not found: {mods_config_path}")
        mc = ET.fromstring(mods_config_path.read_text(encoding="utf-8-sig"))
        am = mc.find("activeMods")
        if am is None:
            am = ET.SubElement(mc, "activeMods")
        existing = {li.text.strip().lower() for li in am.findall("li") if li.text}
        for req_mod in ("brrainz.rimbridgeserver", "sanguo.giantcreatureframework"):
            if req_mod not in existing:
                ET.SubElement(am, "li").text = req_mod
                existing.add(req_mod)
        print(f"[ARENA_CLI] Loaded custom ModsConfig ({len(am.findall('li'))} mods) from {mods_config_path}", flush=True)
    else:
        mc = ET.Element("ModsConfigData")
        ET.SubElement(mc, "version").text = "1.7.5014 rev1195"
        am = ET.SubElement(mc, "activeMods")
        for m in ARENA_MODS:
            ET.SubElement(am, "li").text = m
        ke = ET.SubElement(mc, "knownExpansions")
        for e in KNOWN_EXPANSIONS:
            ET.SubElement(ke, "li").text = e
    (cfg_dir / "ModsConfig.xml").write_bytes(ET.tostring(mc, encoding="utf-8", xml_declaration=True))

    gabs_dir.mkdir(parents=True, exist_ok=True)
    args_list = [
        f"-savedatafolder={runtime_dir.as_posix()}",
        "-fast",
        "-quicktest",
        "-popupwindow",
        "-screen-width",
        str(width),
        "-screen-height",
        str(height),
    ]
    if extra_launch_args:
        args_list.extend(extra_launch_args)

    gabs_cfg = {
        "version": "1.0",
        "games": {
            game_id: {
                "id": game_id,
                "name": "RimWorld Giant Creature Arena",
                "launchMode": "DirectPath",
                "target": rimworld_exe.as_posix(),
                "workingDir": rimworld_exe.parent.as_posix(),
                "stopProcessName": "RimWorldWin64",
                "args": args_list,
                "defaultProfile": "default",
                "profiles": {"default": {"description": "Giant Arena Match Profile"}},
            }
        },
    }
    (gabs_dir / "config.json").write_text(json.dumps(gabs_cfg, indent=2), encoding="utf-8")


def wait_for_gabs_http(port: int, timeout_s: float = 20.0):
    deadline = time.time() + timeout_s
    while time.time() < deadline:
        try:
            c = http.client.HTTPConnection("127.0.0.1", port, timeout=2)
            c.request(
                "POST",
                "/mcp",
                json.dumps({"jsonrpc": "2.0", "id": 1, "method": "tools/list", "params": {}}).encode("utf-8"),
                {"Content-Type": "application/json"},
            )
            r = c.getresponse()
            r.read()
            c.close()
            if r.status == 200:
                return
        except Exception:
            time.sleep(0.25)
    raise RuntimeError(f"GABS HTTP server on port {port} failed to start within {timeout_s:.1f}s.")


def wait_for_rimworld_playing(client: GabsClient, timeout_s: float = 150.0):
    t0 = time.time()
    deadline = t0 + timeout_s
    attempt = 0
    while time.time() < deadline:
        attempt += 1
        try:
            cand = client.rw("rimworld_get_game_info", {}, timeout=15)
            st = cand.get("state", {}) if isinstance(cand, dict) else {}
            ps = st.get("programState") or cand.get("programState") or ""
            if ps == "Playing":
                print(f"[ARENA_CLI] RimWorld Playing ready in {time.time() - t0:.1f}s", flush=True)
                return
            if attempt == 20 and ps == "Entry":
                client.rw("rimworld_start_debug_game_ready", {"timeoutMs": 90000}, timeout=110)
        except Exception:
            try:
                client.call_mcp("games_connect", {"gameId": client.game_id}, timeout=20)
            except Exception:
                pass
            client.drain_attention_if_any()
        time.sleep(1.0)
    raise RuntimeError(f"Timed out waiting for RimWorld Playing state after {timeout_s:.1f}s.")


def unwrap_result(resp: dict) -> dict:
    if isinstance(resp, dict) and isinstance(resp.get("result"), dict):
        return resp["result"]
    return resp if isinstance(resp, dict) else {}


def pull_logs(client: GabsClient, after_seq: int = 0):
    all_logs = []
    cur = after_seq
    for _ in range(15):
        res = client.rw("rimbridge_list_logs", {"afterSequence": cur, "limit": 400})
        batch = res.get("logs", []) if isinstance(res, dict) else []
        if not batch:
            break
        all_logs.extend(batch)
        max_s = max((e.get("Sequence", 0) for e in batch), default=cur)
        if max_s <= cur or len(batch) < 400:
            cur = max(cur, max_s)
            break
        cur = max_s
    all_logs.sort(key=lambda e: e.get("Sequence", 0))
    return all_logs, cur


def step_ticks(client: GabsClient, ticks: int):
    if ticks <= 0:
        return
    res = client.rw(
        "rimworld_step_game_ticks",
        {
            "ticks": ticks,
            "pauseFirst": True,
            "playSound": False,
            "pollIntervalMs": 5,
            "timeoutMs": 30000,
        },
    )
    if isinstance(res, dict) and not res.get("success", True):
        raise RuntimeError(f"step_ticks({ticks}) failed: {res}")


def capture_frame_png_bytes(client: GabsClient, screenshots_dir: pathlib.Path, stem: str, keep_path: pathlib.Path | None = None) -> bytes:
    """Capture one screenshot via RimBridge, read its PNG bytes, and delete the temp screenshot immediately to keep disk usage near zero."""
    default_src = screenshots_dir / f"{stem}.png"
    for attempt in range(4):
        req_stem = stem if attempt == 0 else f"{stem}_r{attempt}"
        fallback_src = screenshots_dir / f"{req_stem}.png"
        try:
            res = client.rw(
                "rimworld_take_screenshot",
                {
                    "fileName": req_stem,
                    "includeTargets": False,
                    "suppressMessage": True,
                },
                timeout=45,
            )
            src = pathlib.Path(res["path"]) if isinstance(res, dict) and res.get("path") else fallback_src
        except Exception:
            src = fallback_src

        deadline = time.time() + 3.0
        while time.time() < deadline:
            for cand in (src, fallback_src, default_src):
                if cand.exists() and cand.stat().st_size >= 15000:
                    data = cand.read_bytes()
                    if keep_path is not None:
                        keep_path.write_bytes(data)
                    try:
                        cand.unlink()
                    except Exception:
                        pass
                    return data
            time.sleep(0.05)
    raise RuntimeError(f"Failed capturing screenshot {stem}")


def load_font(size: int, bold: bool = False):
    candidates = [
        r"C:\Windows\Fonts\msyhbd.ttc" if bold else r"C:\Windows\Fonts\msyh.ttc",
        r"C:\Windows\Fonts\consolab.ttf" if bold else r"C:\Windows\Fonts\consola.ttf",
        r"C:\Windows\Fonts\arialbd.ttf" if bold else r"C:\Windows\Fonts\arial.ttf",
    ]
    for p in candidates:
        if os.path.exists(p):
            try:
                return ImageFont.truetype(p, size)
            except Exception:
                continue
    return ImageFont.load_default()


def build_contact_sheet_from_thumbs(thumbs: list[tuple[dict, Image.Image]], out_path: pathlib.Path, title: str):
    if not thumbs:
        return
    picks = [round(i * (len(thumbs) - 1) / 11) for i in range(min(12, len(thumbs)))]
    sheet_w, sheet_h = 1600, 960
    sheet = Image.new("RGB", (sheet_w, sheet_h), (14, 17, 24))
    draw = ImageDraw.Draw(sheet)
    ft_title = load_font(20, bold=True)
    ft_cell = load_font(13, bold=False)
    draw.text((16, 10), title, fill=(240, 246, 255), font=ft_title)

    cw, ch = 388, 288
    for slot, ridx in enumerate(picks):
        row, col = divmod(slot, 4)
        x0 = 12 + col * (cw + 8)
        y0 = 44 + row * (ch + 12)
        rec, thumb_src = thumbs[ridx]
        thumb = thumb_src.resize((cw, ch - 24), Image.Resampling.LANCZOS)
        sheet.paste(thumb, (x0, y0))
        lbl = (
            f"#{rec['frame']:03d} +{rec['elapsedTicks']:04d}t | "
            f"A:{rec['sideAHp']*100:4.0f}% vs B:{rec['sideBHp']*100:4.0f}% | {rec['status']}"
        )
        draw.rectangle([x0, y0 + ch - 24, x0 + cw, y0 + ch], fill=(20, 24, 34))
        draw.text((x0 + 6, y0 + ch - 21), lbl, fill=(215, 232, 252), font=ft_cell)

    sheet.save(out_path, optimize=True)


def kill_isolated_rimworld(rimworld_exe: pathlib.Path):
    exe_filter = str(rimworld_exe.parent).replace("'", "''")
    subprocess.run(
        [
            "powershell",
            "-NoProfile",
            "-Command",
            f"Get-CimInstance Win32_Process -Filter \"Name='RimWorldWin64.exe'\" | Where-Object {{ $_.ExecutablePath -like '*{exe_filter}*' }} | ForEach-Object {{ Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }}",
        ],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )


def build_match_payload(args: argparse.Namespace) -> dict:
    payload = {
        "sideAKind": args.side_a,
        "sideBKind": args.side_b,
        "preset": args.preset,
        "sideACount": args.count_a,
        "sideBCount": args.count_b,
        "sideARaidPoints": args.side_a_raid_points,
        "sideBRaidPoints": args.side_b_raid_points,
        "sideAVanillaOnly": args.side_a_vanilla_only,
        "sideBVanillaOnly": args.side_b_vanilla_only,
        "spawnSeparation": args.separation,
        "arenaHalfWidth": args.half_width,
        "arenaHalfHeight": args.half_height,
        "startPaused": args.start_paused,
        "lockNoonClearWeather": True,
        "cleanVideoMode": not args.keep_ui,
        "cinematicHudOnly": not args.keep_ui,
        "enableVersusHud": not args.no_versus_hud,
        "hideDevToolbar": not args.keep_ui,
        "boardCrewForVehicles": args.board_crew,
        "cameraRootSize": args.camera_size,
    }
    if args.label_a:
        payload["sideALabel"] = args.label_a
    if args.label_b:
        payload["sideBLabel"] = args.label_b
    if args.faction_a:
        payload["sideAFactionDef"] = args.faction_a
    if args.faction_b:
        payload["sideBFactionDef"] = args.faction_b
    return payload


def close_all_startup_windows(client: GabsClient):
    """Close any mod notice/welcome dialogs opened at startup before recording."""
    for _ in range(15):
        try:
            ui = unwrap_result(client.rw("rimworld_get_ui_state", {}))
            wins = ui.get("windows") or ui.get("openWindows") or []
            if not wins:
                break
            client.rw("rimworld_close_window", {})
        except Exception:
            break


def cmd_presets(_args: argparse.Namespace):
    for pid, desc in BUILT_IN_PRESETS:
        print(f"  {pid:16s} - {desc}")


def cmd_launch(args: argparse.Namespace):
    rimworld_exe = pathlib.Path(args.rimworld_exe)
    runtime_dir = pathlib.Path(args.runtime_dir)
    gabs_dir = pathlib.Path(args.gabs_dir)
    mods_cfg_path = pathlib.Path(args.mods_config) if args.mods_config else None

    if args.mode == "direct":
        cfg_file = runtime_dir / "arena_launch_config.json"
        prepare_runtime_dir(
            runtime_dir=runtime_dir,
            gabs_dir=gabs_dir,
            rimworld_exe=rimworld_exe,
            game_id=args.game_id,
            width=args.width,
            height=args.height,
            lang=args.lang,
            mods_config_path=mods_cfg_path,
        )
        match_cfg = build_match_payload(args)
        cfg_file.write_text(json.dumps(match_cfg, indent=2), encoding="utf-8")
        launch_cmd = [
            str(rimworld_exe),
            f"-savedatafolder={runtime_dir.as_posix()}",
            "-fast",
            "-quicktest",
            f"-gcfarena={cfg_file.as_posix()}",
            "-popupwindow",
            "-screen-width",
            str(args.width),
            "-screen-height",
            str(args.height),
        ]
        print("[ARENA_CLI] Direct launching RimWorld into preset arena:", " ".join(launch_cmd), flush=True)
        subprocess.Popen(launch_cmd, cwd=str(rimworld_exe.parent))
        return

    prepare_runtime_dir(
        runtime_dir=runtime_dir,
        gabs_dir=gabs_dir,
        rimworld_exe=rimworld_exe,
        game_id=args.game_id,
        width=args.width,
        height=args.height,
        lang=args.lang,
        mods_config_path=mods_cfg_path,
    )
    gabs_exe = resolve_gabs_exe(args.gabs_exe)
    gabs_proc = subprocess.Popen(
        [str(gabs_exe), "server", "http", "--addr", f"127.0.0.1:{args.port}", "--configDir", str(gabs_dir)],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )
    client = GabsClient("127.0.0.1", args.port, game_id=args.game_id)
    wait_for_gabs_http(args.port, timeout_s=20.0)
    try:
        client.call_mcp("games_start", {"gameId": args.game_id}, timeout=180)
    except Exception as ex:
        print(f"[ARENA_CLI] games_start note: {ex}", flush=True)

    wait_for_rimworld_playing(client, timeout_s=float(args.startup_timeout))
    client.drain_attention_if_any()
    close_all_startup_windows(client)
    client.rw("rimworld_set_camera_zoom_extension", {"enabled": True})

    setup_res = unwrap_result(client.rw("gcf_arena/setup_match", build_match_payload(args)))
    print("[ARENA_CLI] GABS_READY=" + json.dumps({
        "gabsUrl": f"http://127.0.0.1:{args.port}/mcp",
        "gameId": args.game_id,
        "gabsPid": gabs_proc.pid,
        "setup": setup_res,
    }, ensure_ascii=False), flush=True)

    if args.wait:
        print("[ARENA_CLI] Press Ctrl+C to stop GABS & RimWorld.", flush=True)
        try:
            gabs_proc.wait()
        except KeyboardInterrupt:
            try:
                client.call_mcp("games_stop", {"gameId": args.game_id}, timeout=20)
            except Exception:
                pass
            gabs_proc.terminate()


def build_contact_sheet_from_mp4(mp4_path: pathlib.Path, out_path: pathlib.Path, title: str, total_seconds: float, ffmpeg_exe: pathlib.Path | None):
    if not mp4_path.exists() or ffmpeg_exe is None or not ffmpeg_exe.exists() or total_seconds <= 0.1:
        return
    tmp_dir = out_path.parent / "_sheet_tmp"
    if tmp_dir.exists():
        shutil.rmtree(tmp_dir)
    tmp_dir.mkdir(parents=True, exist_ok=True)
    try:
        sheet_w, sheet_h = 1600, 960
        sheet = Image.new("RGB", (sheet_w, sheet_h), (14, 17, 24))
        draw = ImageDraw.Draw(sheet)
        ft_title = load_font(20, bold=True)
        ft_cell = load_font(13, bold=False)
        draw.text((16, 10), title, fill=(240, 246, 255), font=ft_title)

        cw, ch = 388, 288
        for slot in range(12):
            ts = max(0.05, min(total_seconds - 0.15, (slot / 11.0) * max(0.5, total_seconds - 0.2)))
            f_png = tmp_dir / f"s_{slot:02d}.png"
            subprocess.run(
                [
                    str(ffmpeg_exe),
                    "-y",
                    "-ss",
                    f"{ts:.3f}",
                    "-i",
                    str(mp4_path),
                    "-frames:v",
                    "1",
                    str(f_png),
                ],
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
            )
            if not f_png.exists():
                continue
            row, col = divmod(slot, 4)
            x0 = 12 + col * (cw + 8)
            y0 = 44 + row * (ch + 12)
            with Image.open(f_png) as im:
                thumb = im.convert("RGB").resize((cw, ch - 24), Image.Resampling.LANCZOS)
            sheet.paste(thumb, (x0, y0))
            draw.rectangle([x0, y0 + ch - 24, x0 + cw, y0 + ch], fill=(20, 24, 34))
            draw.text((x0 + 6, y0 + ch - 21), f"#{slot+1:02d}/12  t={ts:5.1f}s / {total_seconds:5.1f}s", fill=(215, 232, 252), font=ft_cell)
        sheet.save(out_path, optimize=True)
    finally:
        shutil.rmtree(tmp_dir, ignore_errors=True)


def cmd_record(args: argparse.Namespace):
    rimworld_exe = pathlib.Path(args.rimworld_exe).resolve()
    runtime_dir = pathlib.Path(args.runtime_dir).resolve()
    gabs_dir = pathlib.Path(args.gabs_dir).resolve()
    out_dir = pathlib.Path(args.out).resolve()
    raw_dir = out_dir / "raw_frames"
    screenshots_dir = runtime_dir / "Screenshots"
    mods_cfg_path = pathlib.Path(args.mods_config) if args.mods_config else None
    mp4_path = out_dir / "match.mp4"
    sheet_path = out_dir / "contact_sheet.png"
    report_path = out_dir / "match_report.json"

    if raw_dir.exists():
        shutil.rmtree(raw_dir)
    out_dir.mkdir(parents=True, exist_ok=True)
    if args.keep_raw_frames:
        raw_dir.mkdir(parents=True, exist_ok=True)

    playback_speed = (args.ticks_per_frame * args.fps) / 60.0
    print(
        f"[ARENA_CLI] Recording config ({args.engine}): {args.side_a} vs {args.side_b} ({args.preset}) | "
        f"{args.width}x{args.height} @ {args.fps}fps, {args.ticks_per_frame} ticks/frame => {playback_speed:.2f}x real-time | "
        f"postKO={args.post_ko_seconds:.1f}s",
        flush=True,
    )

    prepare_runtime_dir(
        runtime_dir=runtime_dir,
        gabs_dir=gabs_dir,
        rimworld_exe=rimworld_exe,
        game_id=args.game_id,
        width=args.width,
        height=args.height,
        lang=args.lang,
        mods_config_path=mods_cfg_path,
    )

    gabs_exe = resolve_gabs_exe(args.gabs_exe)
    ffmpeg_exe = resolve_ffmpeg_exe(args.ffmpeg_exe)

    gabs_proc = subprocess.Popen(
        [str(gabs_exe), "server", "http", "--addr", f"127.0.0.1:{args.port}", "--configDir", str(gabs_dir)],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )
    client = GabsClient("127.0.0.1", args.port, game_id=args.game_id)
    records: list[dict] = []
    thumbs: list[tuple[dict, Image.Image]] = []
    setup_state: dict = {}
    final_state: dict = {}
    native_status: dict = {}
    gcf_errors: list[str] = []
    ffmpeg_proc: subprocess.Popen | None = None

    def start_ffmpeg_pipe() -> subprocess.Popen | None:
        if ffmpeg_exe is None or not ffmpeg_exe.exists():
            return None
        return subprocess.Popen(
            [
                str(ffmpeg_exe),
                "-y",
                "-f",
                "image2pipe",
                "-vcodec",
                "png",
                "-framerate",
                str(args.fps),
                "-i",
                "-",
                "-vf",
                "scale=trunc(iw/2)*2:trunc(ih/2)*2",
                "-c:v",
                "libx264",
                "-preset",
                "medium",
                "-crf",
                str(args.crf),
                "-pix_fmt",
                "yuv420p",
                "-movflags",
                "+faststart",
                str(mp4_path),
            ],
            stdin=subprocess.PIPE,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )

    def push_frame(png_bytes: bytes, rec: dict):
        nonlocal ffmpeg_proc
        if ffmpeg_proc is None:
            ffmpeg_proc = start_ffmpeg_pipe()
        if ffmpeg_proc is not None and ffmpeg_proc.stdin is not None:
            ffmpeg_proc.stdin.write(png_bytes)
        with Image.open(io.BytesIO(png_bytes)) as im:
            thumb = im.convert("RGB").resize((400, 225), Image.Resampling.LANCZOS)
        thumbs.append((rec, thumb))

    try:
        wait_for_gabs_http(args.port, timeout_s=20.0)
        try:
            client.call_mcp("games_start", {"gameId": args.game_id}, timeout=180)
        except Exception as ex:
            print(f"[ARENA_CLI] games_start note: {ex}", flush=True)

        wait_for_rimworld_playing(client, timeout_s=float(args.startup_timeout))
        client.drain_attention_if_any()
        close_all_startup_windows(client)
        client.rw("rimworld_pause_game", {"pause": True})
        client.rw("rimworld_set_time_speed", {"speed": "Paused"})
        client.rw("rimworld_set_camera_zoom_extension", {"enabled": True})

        payload = build_match_payload(args)
        payload["startPaused"] = True
        setup_res = unwrap_result(client.rw("gcf_arena/setup_match", payload))
        if not setup_res.get("success", False):
            raise RuntimeError(f"gcf_arena/setup_match failed: {setup_res}")
        setup_state = setup_res.get("state", {})

        client.rw("rimworld_clear_selection", {})
        client.rw("rimworld_clear_hover_target", {})
        client.rw("gcf_arena/frame_camera", {
            "smoothFactor": 1.0,
            "paddingCells": args.camera_padding,
            "minRootSize": args.camera_min_size,
            "maxRootSize": args.camera_max_size,
        })

        # Warm up 2 ticks so initial shadows, headings, and HUD settle cleanly
        step_ticks(client, 2)

        if args.engine == "native":
            rec_res = unwrap_result(client.rw("gcf_arena/start_native_recording", {
                "outputPath": str(mp4_path),
                "ffmpegPath": str(ffmpeg_exe) if ffmpeg_exe else "",
                "fps": args.fps,
                "ticksPerFrame": args.ticks_per_frame,
                "introSeconds": args.intro_seconds,
                "maxMatchSeconds": args.max_match_seconds,
                "postKoSeconds": args.post_ko_seconds,
                "trackCamera": args.track_camera,
                "cameraSmooth": args.camera_smooth,
                "cameraPadding": args.camera_padding,
                "cameraMinSize": args.camera_min_size,
                "cameraMaxSize": args.camera_max_size,
                "crf": args.crf,
            }))
            if not rec_res.get("success", False):
                raise RuntimeError(f"gcf_arena/start_native_recording failed: {rec_res}")

            last_log_frames = -1
            deadline = time.time() + max(900.0, args.max_match_seconds * 12.0)
            while time.time() < deadline:
                time.sleep(2.0)
                st_res = unwrap_result(client.rw("gcf_arena/get_recording_status", {}, timeout=60))
                st = st_res.get("status", {})
                native_status = st
                fc = int(st.get("FramesCaptured", 0))
                if fc != last_log_frames:
                    last_log_frames = fc
                    print(
                        f"[ARENA_CLI] native frames={fc} ({st.get('RecordedSeconds', 0.0):.1f}s @ {st.get('Fps', args.fps)}fps) | "
                        f"A={st.get('SideAHp', 0.0)*100:.1f}% B={st.get('SideBHp', 0.0)*100:.1f}% | "
                        f"status={st.get('MatchStatus', '')} winner={st.get('WinnerSide', 'None')} postKO={st.get('PostKoFramesRemaining', -1)}",
                        flush=True,
                    )
                if st.get("Finished", False):
                    if not st.get("Succeeded", False):
                        raise RuntimeError(f"Native arena recorder failed: {st.get('Error')}")
                    break
            else:
                client.rw("gcf_arena/stop_native_recording", {}, timeout=60)
                raise RuntimeError("Timed out waiting for native arena recording to complete.")

            final_state = unwrap_result(client.rw("gcf_arena/get_match_state", {})).get("state", {})
        else:
            frame_idx = 0
            post_ko_remaining = -1
            stamp = int(time.time() * 10) % 100000
            intro_frames = max(1, int(round(args.intro_seconds * args.fps)))
            max_frames = max(1, int(round(args.max_match_seconds * args.fps)))
            post_ko_frames = max(0, int(round(args.post_ko_seconds * args.fps)))

            for _ in range(intro_frames):
                st = unwrap_result(client.rw("gcf_arena/get_match_state", {})).get("state", {})
                keep_path = (raw_dir / f"frame_{frame_idx:05d}.png") if args.keep_raw_frames else None
                png_bytes = capture_frame_png_bytes(client, screenshots_dir, f"arena_{stamp}_{frame_idx:05d}", keep_path=keep_path)
                rec = {
                    "frame": frame_idx,
                    "elapsedTicks": st.get("ElapsedTicks", 0),
                    "status": st.get("Status", "Ready"),
                    "winnerSide": st.get("WinnerSide", "None"),
                    "sideAHp": st.get("SideATotalHealthPercent", 1.0),
                    "sideBHp": st.get("SideBTotalHealthPercent", 1.0),
                    "minDist": st.get("MinCrossSideDistance", -1.0),
                }
                records.append(rec)
                push_frame(png_bytes, rec)
                frame_idx += 1
                step_ticks(client, 1)

            for _ in range(max_frames):
                step_ticks(client, args.ticks_per_frame)
                if args.track_camera:
                    client.rw("gcf_arena/frame_camera", {
                        "smoothFactor": args.camera_smooth,
                        "paddingCells": args.camera_padding,
                        "minRootSize": args.camera_min_size,
                        "maxRootSize": args.camera_max_size,
                    })

                st = unwrap_result(client.rw("gcf_arena/get_match_state", {})).get("state", {})
                final_state = st

                keep_path = (raw_dir / f"frame_{frame_idx:05d}.png") if args.keep_raw_frames else None
                png_bytes = capture_frame_png_bytes(client, screenshots_dir, f"arena_{stamp}_{frame_idx:05d}", keep_path=keep_path)

                rec = {
                    "frame": frame_idx,
                    "elapsedTicks": st.get("ElapsedTicks", 0),
                    "status": st.get("Status", ""),
                    "winnerSide": st.get("WinnerSide", "None"),
                    "sideAHp": st.get("SideATotalHealthPercent", 0.0),
                    "sideBHp": st.get("SideBTotalHealthPercent", 0.0),
                    "minDist": st.get("MinCrossSideDistance", -1.0),
                }
                records.append(rec)
                push_frame(png_bytes, rec)
                frame_idx += 1

                if frame_idx % 20 == 0:
                    print(
                        f"[ARENA_CLI] frame={frame_idx} tick=+{rec['elapsedTicks']} "
                        f"A={rec['sideAHp']*100:.1f}% B={rec['sideBHp']*100:.1f}% "
                        f"dist={rec['minDist']:.1f} status={rec['status']}",
                        flush=True,
                    )

                if post_ko_remaining < 0 and rec["status"] in ("Finished", "Draw"):
                    post_ko_remaining = post_ko_frames
                    print(f"[ARENA_CLI] Match concluded: status={rec['status']} winner={rec['winnerSide']} at tick +{rec['elapsedTicks']}", flush=True)
                elif post_ko_remaining >= 0:
                    post_ko_remaining -= 1
                    if post_ko_remaining <= 0:
                        break

        all_logs, _ = pull_logs(client, 0)
        gcf_errors = [
            (e.get("Message", "") or "")[:240]
            for e in all_logs
            if str(e.get("Level", "")).lower() in ("error", "fatal")
            and any(k in ((e.get("Message", "") or "") + (e.get("StackTrace", "") or "")) for k in ("GiantCreature", "GCF_", "CompGiant", "GiantArena"))
        ]
    finally:
        if ffmpeg_proc is not None:
            try:
                if ffmpeg_proc.stdin is not None:
                    ffmpeg_proc.stdin.close()
                ffmpeg_proc.wait(timeout=60)
            except Exception:
                ffmpeg_proc.kill()
        try:
            client.call_mcp("games_stop", {"gameId": args.game_id}, timeout=20)
        except Exception:
            pass
        kill_isolated_rimworld(rimworld_exe)
        gabs_proc.terminate()
        try:
            gabs_proc.wait(timeout=8)
        except Exception:
            gabs_proc.kill()

    total_frames = int(native_status.get("FramesCaptured", 0)) if args.engine == "native" else len(records)
    total_seconds = (total_frames / float(args.fps)) if args.fps > 0 else 0.0
    title = f"Giant Arena ({args.fps}fps 1.00x): {args.side_a} (Red) vs {args.side_b} (Blue) [{args.preset}]"
    if args.engine == "native":
        build_contact_sheet_from_mp4(mp4_path, sheet_path, title, total_seconds, ffmpeg_exe)
    elif thumbs:
        build_contact_sheet_from_thumbs(thumbs, sheet_path, title)

    report = {
        "engine": args.engine,
        "sideA": args.side_a,
        "sideB": args.side_b,
        "preset": args.preset,
        "totalFrames": total_frames,
        "durationSeconds": round(total_seconds, 2),
        "fps": args.fps,
        "ticksPerFrame": args.ticks_per_frame,
        "playbackSpeed": round(playback_speed, 3),
        "postKoSeconds": args.post_ko_seconds,
        "mp4Path": str(mp4_path) if mp4_path.exists() else "",
        "mp4Bytes": mp4_path.stat().st_size if mp4_path.exists() else 0,
        "contactSheetPath": str(sheet_path) if sheet_path.exists() else "",
        "gcfErrorCount": len(gcf_errors),
        "gcfErrors": gcf_errors[:10],
        "nativeRecordingStatus": native_status,
        "setupState": setup_state,
        "finalState": final_state,
    }
    report_path.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")
    print("[ARENA_CLI] REPORT=" + json.dumps({
        "engine": args.engine,
        "frames": report["totalFrames"],
        "durationSeconds": report["durationSeconds"],
        "fps": args.fps,
        "playbackSpeed": report["playbackSpeed"],
        "winner": final_state.get("WinnerSide", "None"),
        "status": final_state.get("Status", ""),
        "sideAFinalHp": final_state.get("SideATotalHealthPercent", 0.0),
        "sideBFinalHp": final_state.get("SideBTotalHealthPercent", 0.0),
        "mp4Path": report["mp4Path"],
        "mp4Bytes": report["mp4Bytes"],
        "contactSheetPath": report["contactSheetPath"],
        "gcfErrorCount": report["gcfErrorCount"],
    }, ensure_ascii=False), flush=True)

    if gcf_errors and not args.allow_errors:
        raise RuntimeError(f"GCF runtime errors detected during arena match: {gcf_errors[0]}")


def add_common_match_args(p: argparse.ArgumentParser):
    p.add_argument("--side-a", default="GCF_MechScorpion", help="PawnKindDef for Side A (West / Red).")
    p.add_argument("--side-b", default="GCF_MechScorpion", help="PawnKindDef for Side B (East / Blue).")
    p.add_argument("--preset", default="Colosseum", help="Preset arena: Colosseum, Plains, DesertRuins, Tundra, VolcanicBasalt.")
    p.add_argument("--count-a", type=int, default=1, help="Number of Side A units (1-8).")
    p.add_argument("--count-b", type=int, default=1, help="Number of Side B units (1-8).")
    p.add_argument("--side-a-raid-points", type=float, default=0.0, help="Spawn Side A as a RimWorld raid-points army (e.g. 5000).")
    p.add_argument("--side-b-raid-points", type=float, default=0.0, help="Spawn Side B as a RimWorld raid-points army (e.g. 5000).")
    p.add_argument("--side-a-vanilla-only", action="store_true", help="Restrict Side A raid-points pool to official vanilla/DLC units only.")
    p.add_argument("--side-b-vanilla-only", action="store_true", help="Restrict Side B raid-points pool to official vanilla/DLC units only.")
    p.add_argument("--label-a", default="", help="Optional display title override for Side A.")
    p.add_argument("--label-b", default="", help="Optional display title override for Side B.")
    p.add_argument("--faction-a", default="", help="Optional FactionDef for Side A.")
    p.add_argument("--faction-b", default="", help="Optional FactionDef for Side B.")
    p.add_argument("--separation", type=int, default=30, help="Spawn separation in cells between Side A and Side B.")
    p.add_argument("--half-width", type=int, default=48, help="Inner arena half-width in cells.")
    p.add_argument("--half-height", type=int, default=40, help="Inner arena half-height in cells.")
    p.add_argument("--board-crew", action="store_true", help="Board AI crew into cockpit vehicles.")
    p.add_argument("--camera-size", type=float, default=0.0, help="Explicit initial camera rootSize (0=auto).")
    p.add_argument("--keep-ui", action="store_true", help="Keep standard RimWorld HUD/DevMode UI visible.")
    p.add_argument("--no-versus-hud", action="store_true", help="Disable top Red vs Blue Versus Boss Health Bar HUD.")
    p.add_argument("--width", type=int, default=1600, help="Window width in pixels.")
    p.add_argument("--height", type=int, default=900, help="Window height in pixels.")
    p.add_argument("--lang", default="", help="Optional RimWorld langFolderName override (empty preserves user Prefs.xml language).")
    p.add_argument("--rimworld-exe", default=str(DEFAULT_RIMWORLD_EXE), help="Path to RimWorldWin64.exe.")
    p.add_argument("--gabs-exe", default="", help="Path to gabs executable (defaults to GABS_EXE env var or PATH).")
    p.add_argument("--ffmpeg-exe", default="", help="Path to ffmpeg executable (defaults to FFMPEG_EXE env var or PATH).")
    p.add_argument("--mods-config", default="", help="Optional path to an existing ModsConfig.xml (e.g. full multi-race playset).")
    p.add_argument("--startup-timeout", type=int, default=180, help="Timeout in seconds waiting for RimWorld Playing state.")
    p.add_argument("--runtime-dir", default=str(DEFAULT_RUNTIME_DIR), help="Isolated savedatafolder path.")
    p.add_argument("--gabs-dir", default=str(DEFAULT_GABS_DIR), help="GABS configDir path.")
    p.add_argument("--game-id", default="rimworld-giant-arena", help="GABS gameId.")
    p.add_argument("--port", type=int, default=8104, help="GABS HTTP server port.")


def main():
    parser = argparse.ArgumentParser(description="Giant Creature Arena launcher and automated versus video recorder.")
    sub = parser.add_subparsers(dest="command", required=True)

    p_presets = sub.add_parser("presets", help="List built-in preset arena maps.")
    p_presets.set_defaults(func=cmd_presets)

    p_launch = sub.add_parser("launch", help="Launch RimWorld straight into a preset arena with two hostile giants.")
    add_common_match_args(p_launch)
    p_launch.add_argument("--mode", choices=("gabs", "direct"), default="gabs", help="Launch via GABS MCP bridge or direct CLI -gcfarena.")
    p_launch.add_argument("--start-paused", action="store_true", help="Start match paused (default unpaused for direct/interactive launch).")
    p_launch.add_argument("--wait", action="store_true", help="Keep CLI alive until Ctrl+C when using --mode gabs.")
    p_launch.set_defaults(func=cmd_launch)

    p_rec = sub.add_parser("record", help="Launch via GABS and record an automated giant-vs-giant battle video.")
    add_common_match_args(p_rec)
    p_rec.add_argument("--out", required=True, help="Output directory for raw_frames, match.mp4, contact_sheet.png, and match_report.json.")
    p_rec.add_argument("--start-paused", action="store_true", default=True, help=argparse.SUPPRESS)
    p_rec.add_argument("--engine", choices=("native", "screenshot"), default="native", help="Capture backend: native (60fps in-process GPU->FFmpeg pipe) or screenshot (legacy MCP loop).")
    p_rec.add_argument("--intro-seconds", type=float, default=1.5, help="Opening face-off duration in seconds.")
    p_rec.add_argument("--max-match-seconds", type=float, default=180.0, help="Maximum match recording duration in seconds.")
    p_rec.add_argument("--post-ko-seconds", type=float, default=5.0, help="Seconds to keep recording after KO (default 5.0s).")
    p_rec.add_argument("--ticks-per-frame", type=int, default=1, help="Simulation ticks stepped per rendered frame (1 tick @ 60fps = exact 1.00x real-time).")
    p_rec.add_argument("--fps", type=int, default=60, help="Output MP4 framerate (default 60 fps).")
    p_rec.add_argument("--crf", type=int, default=17, help="FFmpeg libx264 CRF quality (lower = sharper, default 17).")
    p_rec.add_argument("--track-camera", action="store_true", default=True, help="Smoothly track both fighters with dynamic camera zoom.")
    p_rec.add_argument("--no-track-camera", dest="track_camera", action="store_false", help="Keep camera fixed at initial framing.")
    p_rec.add_argument("--camera-smooth", type=float, default=0.07, help="Per-frame camera tracking lerp factor (default 0.07 at 60fps).")
    p_rec.add_argument("--camera-padding", type=float, default=10.5, help="Camera margin around both fighters in cells.")
    p_rec.add_argument("--camera-min-size", type=float, default=16.0, help="Closest camera rootSize.")
    p_rec.add_argument("--camera-max-size", type=float, default=44.0, help="Widest camera rootSize.")
    p_rec.add_argument("--keep-raw-frames", action="store_true", help="Keep individual PNG files under raw_frames/ (default streams straight to ffmpeg to save disk space).")
    p_rec.add_argument("--allow-errors", action="store_true", help="Do not fail exit code if GCF log errors occur.")
    p_rec.set_defaults(func=cmd_record)

    args = parser.parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
