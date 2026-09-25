r"""
生成 data/app-catalog.json —— 离线「应用名册」。

职责划分（很重要）：
    packages.json / protected.json  →  决策：删 / 留 / 怎么删（人工把关，写错会误删）
    app-catalog.json                →  描述：这是什么（批量生成，写错只是显示不对）

数据来源：Universal-Debloat-Alliance 的 uad_lists.json（社区长期维护，5381 条）。
它给的是「包名 + 英文说明 + removal 结论」，没有中文，所以这里要批量机翻。
默认走本机的 Hy-MT2 翻译服务（7B，一次一批 + json_schema 结构化输出），
比免费接口快得多也不会被限流。

用法：
    python tools/build-app-catalog.py                     # 默认用本机 Hy-MT2 服务翻译
    python tools/build-app-catalog.py --translator google # 没有本地服务时退回免费接口（会被限速）
    python tools/build-app-catalog.py --translator none   # 完全不翻译，只生成结构
    python tools/build-app-catalog.py --refresh           # 重新下载 UAD 清单

翻译结果缓存在 tools/catalog-i18n-cache.json，重复运行只会补没翻过的。
人工校正写在 tools/app-catalog-zh.json，优先级最高。

本机 Hy-MT2 服务见 \\wsl.localhost\Ubuntu-22.04\home\by\hy-mt2\AGENT_API.md：
    wsl -u by -d Ubuntu-22.04 -- bash -lc "cd ~/hy-mt2 && ./bin/hy up 7b"
服务不自启，脚本会先探活；没起来就跳过翻译（保留英文原文），不会让构建失败。
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
import time
import urllib.parse
import urllib.request
from concurrent.futures import ThreadPoolExecutor
from datetime import date

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DIST = os.path.join(ROOT, "dist")
UAD_URL = ("https://raw.githubusercontent.com/Universal-Debloater-Alliance/"
           "universal-android-debloater-next-generation/main/resources/assets/uad_lists.json")

UAD_CACHE = os.path.join(DIST, "uad_lists.json")
# 译文缓存放在 tools/ 下并随仓库提交：它是内容而不是临时产物，
# 换台机器继续翻译时不必从头再来（免费接口限速很凶，重来一次要好几个小时）。
I18N_CACHE = os.path.join(ROOT, "tools", "catalog-i18n-cache.json")
OVERRIDES = os.path.join(ROOT, "tools", "app-catalog-zh.json")
OUTPUT = os.path.join(ROOT, "data", "app-catalog.json")

TRANS_URL = "https://translate.googleapis.com/translate_a/single"
TRANS_HEADERS = {"User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64)"}
MAX_SUMMARY = 160
MAX_UNTRANSLATED = 220
WORKERS = 6

# 本机 Hy-MT2（llama.cpp，OpenAI 兼容接口）
HYMT2_BASE = "http://localhost:8002"
HYMT2_MODEL = "hy-mt2-7b"
HYMT2_BATCH = 30          # 实测 30 条 ≈ 28s，prompt+completion 都在 8192 上下文内
HYMT2_SAMPLING = {"temperature": 0.7, "top_p": 0.6, "top_k": 20, "repetition_penalty": 1.05}

# 翻译优先级：国内机型上真正会出现的包名排在前面。
# 英文接口随时可能限速，所以顺序很关键——按品牌分组，别让 com.android 这种大块头
# 把配额吃光（第一版就吃过这个亏：vivo / OPPO 系全被挤到后面）。
PRIORITY_GROUPS = (
    ("com.miui.", "com.xiaomi."),
    ("com.vivo.", "com.bbk.", "com.iqoo."),
    ("com.oppo.", "com.coloros.", "com.oplus.", "com.oneplus.", "com.realme."),
    ("com.huawei.", "com.hihonor.", "com.honor.", "com.meizu.", "com.transsion.", "cn.nubia."),
    ("com.android.", "com.google.android.", "android."),
)


def priority(package):
    for rank, prefixes in enumerate(PRIORITY_GROUPS):
        if package.startswith(prefixes):
            return rank
    if package.startswith(("com.", "cn.", "org.", "net.")):
        return len(PRIORITY_GROUPS)
    return len(PRIORITY_GROUPS) + 1


def log(message):
    print(message, flush=True)


def download_uad(force=False):
    os.makedirs(DIST, exist_ok=True)
    if os.path.exists(UAD_CACHE) and not force:
        log(f"使用已下载的 UAD 清单：{os.path.relpath(UAD_CACHE, ROOT)}")
        return

    log("下载 UAD 清单…")
    request = urllib.request.Request(UAD_URL, headers=TRANS_HEADERS)
    with urllib.request.urlopen(request, timeout=120) as response:
        data = response.read()
    with open(UAD_CACHE, "wb") as handle:
        handle.write(data)
    log(f"已保存 {len(data) / 1024 / 1024:.2f} MB")


def load_json(path, default):
    if not os.path.exists(path):
        return default
    with open(path, encoding="utf-8") as handle:
        return json.load(handle)


def load_translation_cache():
    return load_json(I18N_CACHE, {})


def save_translation_cache(cache):
    os.makedirs(DIST, exist_ok=True)
    with open(I18N_CACHE, "w", encoding="utf-8") as handle:
        json.dump(cache, handle, ensure_ascii=False, indent=0)


def translate(text):
    """免费接口翻译（备用）。失败返回 None，由调用方回退到英文原文。"""
    if not text or not text.strip():
        return None

    query = urllib.parse.urlencode({"client": "gtx", "sl": "en", "tl": "zh-CN", "dt": "t", "q": text[:1200]})
    request = urllib.request.Request(f"{TRANS_URL}?{query}", headers=TRANS_HEADERS)

    for attempt in range(3):
        try:
            with urllib.request.urlopen(request, timeout=30) as response:
                payload = json.loads(response.read().decode("utf-8", "replace"))
            # 返回结构：[[["译文","原文",...], ...], ...]
            pieces = [seg[0] for seg in payload[0] if seg and seg[0]]
            result = "".join(pieces).strip()
            return result or None
        except Exception:
            time.sleep(1.5 * (attempt + 1))
    return None


class HyMt2Translator:
    """本机 Hy-MT2 翻译服务（llama.cpp，OpenAI 兼容）。

    一次请求翻译一整批，并用 json_schema 强制结构化输出——比逐条调用快得多，
    也不用去正则抠自然语言。7B 只有 1 个槽位，必须串行。
    """

    SCHEMA = {
        "type": "object",
        "properties": {
            "items": {
                "type": "array",
                "items": {
                    "type": "object",
                    "properties": {"id": {"type": "integer"}, "translation": {"type": "string"}},
                    "required": ["id", "translation"],
                },
            }
        },
        "required": ["items"],
    }

    def __init__(self, batch=HYMT2_BATCH, base=HYMT2_BASE, model=HYMT2_MODEL):
        self.batch = batch
        self.base = base
        self.model = model

    def available(self):
        try:
            with urllib.request.urlopen(f"{self.base}/health", timeout=5) as response:
                return json.loads(response.read().decode("utf-8")).get("status") == "ok"
        except Exception:
            return False

    def translate_batch(self, packages, raw):
        """packages 是包名列表，返回 {包名: 译文}。"""
        items = []
        for index, package in enumerate(packages):
            text = clean_summary(raw[package].get("description", ""), MAX_SUMMARY)
            if text:
                items.append({"id": index, "text": text})

        if not items:
            return {}

        prompt = (
            "将下面 JSON 里每条 text 逐条翻译成简体中文，用于说明这个安卓应用是干什么的。"
            "要求：使用简体中文，语气平实；每条不超过 60 个字；不要添加原文没有的信息；"
            "只输出 JSON。\n\n" + json.dumps(items, ensure_ascii=False)
        )

        payload = {
            "model": self.model,
            "messages": [{"role": "user", "content": prompt}],
            "max_tokens": 4096,
            "response_format": {"type": "json_schema", "json_schema": {"name": "t", "schema": self.SCHEMA}},
            **HYMT2_SAMPLING,
        }

        request = urllib.request.Request(
            f"{self.base}/v1/chat/completions",
            data=json.dumps(payload).encode("utf-8"),
            headers={"Content-Type": "application/json"},
        )

        with urllib.request.urlopen(request, timeout=600) as response:
            body = json.loads(response.read().decode("utf-8"))

        content = body["choices"][0]["message"]["content"]
        parsed = json.loads(content)

        result = {}
        for item in parsed.get("items", []):
            index = item.get("id")
            text = (item.get("translation") or "").strip()
            if isinstance(index, int) and 0 <= index < len(packages) and text:
                result[packages[index]] = text
        return result, body.get("usage", {})


def run_hymt2(todo, raw, cache):
    """用本机 Hy-MT2 服务批量翻译。7B 单槽，必须串行。"""
    translator = HyMt2Translator()
    if not translator.available():
        log(f"Hy-MT2 服务没在跑（{HYMT2_BASE}）。启动方式：")
        log('  wsl -u by -d Ubuntu-22.04 -- bash -lc "cd ~/hy-mt2 && ./bin/hy up 7b"')
        log("本次跳过翻译，未翻译的条目会保留英文原文。")
        return

    total = len(todo)
    batches = [todo[i:i + translator.batch] for i in range(0, total, translator.batch)]
    log(f"用 {HYMT2_MODEL} 翻译 {total} 条，每批 {translator.batch} 条，共 {len(batches)} 批（串行）…")

    started = time.time()
    done = 0
    failed = 0

    for index, batch in enumerate(batches, 1):
        try:
            result, usage = translator.translate_batch(batch, raw)
            cache.update(result)
            done += len(batch)
            failed += len(batch) - len(result)
        except Exception as ex:
            failed += len(batch)
            log(f"  第 {index} 批失败：{str(ex)[:120]}")

        if index % 5 == 0 or index == len(batches):
            save_translation_cache(cache)
            elapsed = time.time() - started
            speed = done / elapsed if elapsed else 0
            remain = (total - done) / speed if speed else 0
            log(f"  {done}/{total}  用时 {elapsed / 60:.1f}min  预计剩余 {remain / 60:.1f}min")

    save_translation_cache(cache)
    log(f"Hy-MT2 翻译结束：成功 {done - failed} 条，失败 {failed} 条。")


def run_google(todo, raw, cache):
    """免费接口翻译（备用）。会被限速，只适合少量补翻。"""
    log(f"用免费接口翻译 {len(todo)} 条，并发 {WORKERS}（优先国内机型相关）…")
    done = 0
    started = time.time()

    def work(package):
        text = clean_summary(raw[package].get("description", ""), MAX_SUMMARY)
        return package, translate(text) if text else None

    with ThreadPoolExecutor(max_workers=WORKERS) as pool:
        for package, result in pool.map(work, todo):
            done += 1
            if result:
                cache[package] = result
            if done % 100 == 0 or done == len(todo):
                elapsed = time.time() - started
                speed = done / elapsed if elapsed else 0
                remain = (len(todo) - done) / speed if speed else 0
                log(f"  {done}/{len(todo)}  用时 {elapsed:.0f}s  预计剩余 {remain:.0f}s")
                save_translation_cache(cache)

    save_translation_cache(cache)


def clean_summary(text, limit):
    """UAD 的说明常带换行、链接、多段补充，压成一两句。"""
    if not text:
        return ""
    flat = " ".join(text.split())
    # 去掉链接：UAD 常在说明里塞 Google Play / GitHub 链接，对用户没有意义
    flat = re.sub(r"\(?\s*https?://\S+\s*\)?", " ", flat)
    flat = " ".join(flat.split())
    for marker in ("See https://", "https://github.com", "Read more"):
        index = flat.find(marker)
        if index > 20:
            flat = flat[:index]
    flat = flat.strip(" .。")
    if len(flat) > limit:
        flat = flat[:limit].rstrip() + "…"
    return flat


def collect_translation_targets(entries, cache, overrides, from_rules):
    """挑出需要翻译的包名：已有中文（人工校正 / 规则库）的跳过。"""
    todo = []
    for package, record in entries.items():
        if package in overrides or package in from_rules or package in cache:
            continue
        if record.get("description"):
            todo.append(package)
    return todo


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--refresh", action="store_true", help="重新下载 UAD 清单")
    parser.add_argument("--translator", choices=("hymt2", "google", "none"), default="hymt2",
                        help="翻译方式：hymt2=本机服务（默认），google=免费接口（会被限速），none=不翻译")
    parser.add_argument("--limit", type=int, default=0, help="最多翻译多少条（调试用）")
    args = parser.parse_args()

    download_uad(args.refresh)

    raw = load_json(UAD_CACHE, {})
    if not raw:
        log("错误：拿不到 UAD 清单。")
        return 1
    log(f"UAD 条目：{len(raw)}")

    overrides = load_json(OVERRIDES, {})
    log(f"人工校正（tools/app-catalog-zh.json）：{len(overrides)} 条")

    # 规则库里已经有中文名的，直接拿来用，不必再翻
    from_rules = {}
    for name in ("packages.json", "protected.json"):
        data = load_json(os.path.join(ROOT, "data", name), {})
        for record in data.get("packages", []) + data.get("patterns", []):
            package = record.get("id") or record.get("match")
            text = record.get("reason")
            if package and text and "*" not in package:
                from_rules[package] = text
    log(f"规则库里的中文说明：{len(from_rules)} 条")

    cache = load_translation_cache()
    todo = collect_translation_targets(raw, cache, overrides, from_rules)
    if args.limit:
        todo = todo[: args.limit]

    if args.translator == "none":
        log("已跳过翻译（--translator none）")
    elif not todo:
        log("所有条目都已有译文（缓存命中）")
    elif args.translator == "hymt2":
        run_hymt2(todo, raw, cache)
    else:
        run_google(todo, raw, cache)

    # ---- 组装产物 ----
    entries = {}
    translated = 0
    for package, record in raw.items():
        summary = overrides.get(package) or from_rules.get(package) or cache.get(package)
        if summary:
            translated += 1
            summary = clean_summary(summary, MAX_SUMMARY)

        item = {
            "removal": (record.get("removal") or "").lower(),
            "list": (record.get("list") or "").lower(),
        }
        if summary:
            item["summary"] = summary
        else:
            # 没有译文时保留英文原文：宁可是英文，也不要编一个可能错的中文
            original = clean_summary(record.get("description", ""), MAX_UNTRANSLATED)
            if original:
                item["summaryEn"] = original

        entries[package] = item

    result = {
        "schemaVersion": 1,
        "generatedAt": date.today().isoformat(),
        "source": "Universal-Debloat-Alliance / universal-android-debloater-next-generation 的 uad_lists.json",
        "sourceNote": "removal 是 UAD 社区的结论，不是本项目的判断；summary 由机器翻译，可能有误。",
        "count": len(entries),
        "translated": translated,
        "entries": dict(sorted(entries.items())),
    }

    os.makedirs(os.path.dirname(OUTPUT), exist_ok=True)
    with open(OUTPUT, "w", encoding="utf-8") as handle:
        json.dump(result, handle, ensure_ascii=False, separators=(",", ":"), sort_keys=False)

    size = os.path.getsize(OUTPUT) / 1024 / 1024
    log(f"已生成 {os.path.relpath(OUTPUT, ROOT)}：{len(entries)} 条，其中有中文说明 {translated} 条，{size:.2f} MB")
    return 0


if __name__ == "__main__":
    sys.exit(main())
