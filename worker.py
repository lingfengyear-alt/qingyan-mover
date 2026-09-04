from __future__ import annotations

import argparse
import json
import logging
import re
import sys
import time
import urllib.error
import urllib.request
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


STAGES = [
    "scan_complete",
    "snapany_ready",
    "extraction_complete",
    "player_open",
    "video_download_verified",
    "cover_download_verified",
    "metadata_saved",
    "adspower_ready",
    "reel_editor_ready",
    "media_uploaded",
    "cover_saved",
    "awaiting_publish_confirmation",
]


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="milliseconds").replace("+00:00", "Z")


def load_json(path: Path, default: dict[str, Any]) -> dict[str, Any]:
    if not path.exists():
        return default
    try:
        return json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, json.JSONDecodeError):
        return default


def save_json(path: Path, value: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_suffix(path.suffix + ".tmp")
    temp.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")
    temp.replace(path)


def http_json(url: str, timeout: float = 5) -> dict[str, Any]:
    request = urllib.request.Request(url, headers={"User-Agent": "QingyanMover/0.1"})
    with urllib.request.urlopen(request, timeout=timeout) as response:
        return json.loads(response.read().decode("utf-8"))


class Worker:
    def __init__(self, config: dict[str, Any], dry_run: bool = False):
        self.config = config
        self.dry_run = dry_run or bool(config.get("dry_run"))
        self.state_path = Path(config["state_file"])
        self.log_path = Path(config["log_file"])
        self.state = load_json(self.state_path, {})
        self.log_path.parent.mkdir(parents=True, exist_ok=True)
        logging.basicConfig(
            filename=self.log_path,
            level=logging.INFO,
            format="%(asctime)s %(levelname)s %(message)s",
            encoding="utf-8",
        )

    def persist(self, **updates: Any) -> None:
        self.state.update(updates)
        self.state["last_checked_at"] = utc_now()
        save_json(self.state_path, self.state)

    def stage(self, name: str, evidence: dict[str, Any] | None = None) -> None:
        self.persist(stage=name, stage_evidence=evidence or {}, error_reason=None)
        logging.info("stage=%s evidence=%s", name, evidence or {})

    def fail(self, stage: str, reason: str) -> None:
        self.persist(stage=stage, error_reason=reason, download_status="blocked")
        logging.error("stage=%s failed: %s", stage, reason)

    def adspower_health(self) -> dict[str, Any]:
        base = self.config["adspower"]["api_base"].rstrip("/")
        user_id = self.config["adspower"]["user_id"]
        url = f"{base}/api/v1/browser/start?user_id={urllib.parse.quote(user_id)}"
        try:
            result = http_json(url)
            data = result.get("data") or {}
            return {
                "ok": result.get("code") == 0 and bool(data.get("debug_port")),
                "debug_port": data.get("debug_port"),
                "webdriver": data.get("webdriver"),
            }
        except (urllib.error.URLError, TimeoutError, json.JSONDecodeError) as exc:
            return {"ok": False, "reason": str(exc)}

    def run(self) -> int:
        account = self.config["account"]
        self.persist(account=account, facebook_page=self.config["facebook_page"])
        logging.info("run started dry_run=%s account=%s", self.dry_run, account)

        previous_video = self.state.get("latest_non_pinned_video_id")
        if self.dry_run:
            video_id = previous_video or "DRY_RUN_VIDEO"
            title = self.state.get("page_read_result", {}).get(
                "latest_non_pinned_title", "模拟视频标题"
            )
            self.stage("scan_complete", {"account": account, "video_id": video_id, "pinned_count": 3})
            self.stage("snapany_ready", {"new_tab": True})
            self.stage("extraction_complete", {"video_id": video_id, "title": title})
            self.stage("player_open", {"quality": "1080p"})
            self.stage("video_download_verified", {"artifact": f"douyin_{video_id}.mp4"})
            self.stage("cover_download_verified", {"artifact": f"douyin_{video_id}_cover.jpeg"})
            self.stage("metadata_saved", {"artifact": f"douyin_{video_id}_title.txt"})
            self.stage("adspower_ready", {"dry_run": True})
            self.stage("reel_editor_ready", {"page": self.config["facebook_page"]})
            self.stage("media_uploaded", {"dry_run": True})
            self.stage("cover_saved", {"dry_run": True})
            self.persist(
                stage="awaiting_publish_confirmation",
                download_status="verified",
                facebook_draft_status="ready_unpublished",
                error_reason=None,
            )
            logging.info("run completed awaiting publish confirmation")
            return 0

        health = self.adspower_health()
        if not health["ok"]:
            self.fail("adspower_ready", f"AdsPower unavailable: {health.get('reason', 'no debug port')}")
            return 2

        self.fail(
            "scan_complete",
            "真实浏览器适配器尚未安装。请先完成 dry-run 验证，再接入 Edge/SnapAny 驱动。",
        )
        return 3


def main() -> int:
    parser = argparse.ArgumentParser(description="情焱本地搬运助手")
    parser.add_argument("--config", default="config.json")
    parser.add_argument("--once", action="store_true", help="执行一次后退出")
    parser.add_argument("--dry-run", action="store_true", help="不访问外部平台，只验证状态机")
    args = parser.parse_args()

    config = load_json(Path(args.config), {})
    if not config:
        print(f"配置不存在或无效: {args.config}", file=sys.stderr)
        return 1

    worker = Worker(config, dry_run=args.dry_run)
    if args.once:
        return worker.run()

    while True:
        worker.run()
        time.sleep(max(30, int(config.get("poll_interval_seconds", 1800))))


if __name__ == "__main__":
    raise SystemExit(main())
