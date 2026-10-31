"""Offline RapidOCR JSON-lines worker. Image bytes stay in memory."""

import base64
import json
import os
import sys

sys.stdout.reconfigure(encoding="utf-8")

import cv2
import numpy as np
from rapidocr import RapidOCR


def send(value):
    sys.stdout.write(json.dumps(value, ensure_ascii=False, separators=(",", ":")) + "\n")
    sys.stdout.flush()


def main():
    engine = RapidOCR(params={
        "EngineConfig.onnxruntime.intra_op_num_threads": 2,
        "EngineConfig.onnxruntime.inter_op_num_threads": 1,
    })
    send({"ready": True, "engine": "rapidocr", "pid": os.getpid()})
    for line in sys.stdin:
        request = {}
        try:
            request = json.loads(line)
            raw = base64.b64decode(request["jpeg"], validate=True)
            image = cv2.imdecode(np.frombuffer(raw, np.uint8), cv2.IMREAD_COLOR)
            if image is None:
                raise ValueError("invalid JPEG")
            result = engine(image)
            words = []
            if result.boxes is not None:
                for box, text, score in zip(result.boxes, result.txts, result.scores):
                    points = [[int(point[0]), int(point[1])] for point in box]
                    words.append({"text": text, "score": float(score), "points": points})
            send({"id": request["id"], "words": words, "seconds": float(result.elapse)})
        except Exception as exc:
            send({"id": request.get("id"), "error": str(exc)[:300]})


if __name__ == "__main__":
    main()
