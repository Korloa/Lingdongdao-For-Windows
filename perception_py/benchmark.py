"""Offline OCR/GUI CPU and RSS smoke benchmark; no screenshot is written."""

import argparse
import base64
import io
import json
import os
import subprocess
import sys
import time

import psutil
from PIL import Image, ImageDraw, ImageFont


def sample_image():
    image = Image.new("RGB", (1280, 720), "#f6f7f8")
    draw = ImageDraw.Draw(image)
    draw.rectangle((40, 38, 300, 96), fill="#2463c7")
    draw.rectangle((25, 180, 1150, 590), fill="#1d1d1d")
    font_path = "C:/Windows/Fonts/msyh.ttc"
    font = ImageFont.truetype(font_path, 28) if os.path.isfile(font_path) else ImageFont.load_default()
    draw.text((58, 48), "运行 Run", font=font, fill="white")
    draw.text((60, 225), "Error: module not found", font=font, fill="#ff9494")
    draw.text((60, 285), "未找到文件，请检查路径", font=font, fill="white")
    output = io.BytesIO()
    image.save(output, format="JPEG", quality=85)
    return output.getvalue()


def measure(name, image, model=None):
    script = os.path.join(os.path.dirname(__file__), name + "_worker.py")
    env = os.environ.copy()
    env["OMP_NUM_THREADS"] = "2"
    if model:
        env["LINGDONGDAO_GUI_MODEL"] = model
    started = time.perf_counter()
    worker = subprocess.Popen([sys.executable, "-u", script], stdin=subprocess.PIPE,
                              stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                              text=True, encoding="utf-8", errors="replace", env=env)
    try:
        ready = json.loads(worker.stdout.readline())
        if not ready.get("ready"):
            return {"module": name, "error": ready.get("error", "failed to start")}
        initialization = round(time.perf_counter() - started, 3)
        times = []
        peak_mb = 0.0
        result = {}
        child = psutil.Process(worker.pid)
        def tree():
            return [child, *child.children(recursive=True)]
        def memory_mb():
            return sum(process.memory_info().rss for process in tree()
                       if process.is_running()) / 1048576
        def cpu_seconds():
            return sum((process.cpu_times().user + process.cpu_times().system)
                       for process in tree() if process.is_running())
        cpu_start = cpu_seconds()
        benchmark_start = time.perf_counter()
        for index in range(3):
            started = time.perf_counter()
            worker.stdin.write(json.dumps({"id": index, "jpeg": base64.b64encode(image).decode("ascii")}) + "\n")
            worker.stdin.flush()
            for _ in range(20):
                line = worker.stdout.readline()
                try:
                    result = json.loads(line)
                except json.JSONDecodeError:
                    continue
                if result.get("id") == index:
                    break
            else:
                raise RuntimeError("worker did not return a JSON response")
            times.append(round(time.perf_counter() - started, 3))
            peak_mb = max(peak_mb, memory_mb())
            if "error" in result:
                break
        return {"module": name, "init_seconds": initialization,
                "inference_seconds": times, "peak_worker_mb": round(peak_mb, 1),
                "cpu_core_percent": round(100 * (cpu_seconds() - cpu_start) /
                                          max(0.001, time.perf_counter() - benchmark_start), 1),
                "count": len(result.get("words", result.get("elements", []))),
                "texts": [item["text"] for item in result.get("words", [])],
                "error": result.get("error")}
    finally:
        worker.terminate()
        try:
            worker.wait(timeout=4)
        except subprocess.TimeoutExpired:
            worker.kill()


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", default=os.path.abspath(os.path.join(
        os.path.dirname(__file__), "..", "models", "gpa", "model.onnx")))
    parser.add_argument("--report", help="Optional path for JSON metrics only")
    args = parser.parse_args()
    image = sample_image()
    report = {"image_bytes": len(image), "ocr": measure("ocr", image)}
    if os.path.isfile(args.model):
        report["gui"] = measure("gui", image, args.model)
    else:
        report["gui"] = {"error": "ONNX model not installed"}
    print(json.dumps(report, ensure_ascii=False, indent=2))
    if args.report:
        with open(args.report, "w", encoding="utf-8") as output:
            json.dump(report, output, ensure_ascii=False, indent=2)


if __name__ == "__main__":
    main()
