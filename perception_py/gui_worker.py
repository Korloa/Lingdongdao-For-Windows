"""Optional ONNX CPU detector. Reports only the model's 'icon' class."""

import base64
import json
import os
import sys

sys.stdout.reconfigure(encoding="utf-8")

import cv2
import numpy as np
import onnxruntime as ort


def send(value):
    sys.stdout.write(json.dumps(value, ensure_ascii=False, separators=(",", ":")) + "\n")
    sys.stdout.flush()


def detect(session, image):
    height, width = image.shape[:2]
    size = int(session.get_inputs()[0].shape[2])
    scale = min(size / width, size / height)
    resized_width = round(width * scale)
    resized_height = round(height * scale)
    pad_x = (size - resized_width) // 2
    pad_y = (size - resized_height) // 2
    canvas = np.full((size, size, 3), 114, dtype=np.uint8)
    resized = cv2.resize(image, (resized_width, resized_height), interpolation=cv2.INTER_LINEAR)
    canvas[pad_y:pad_y + resized_height, pad_x:pad_x + resized_width] = resized
    rgb = cv2.cvtColor(canvas, cv2.COLOR_BGR2RGB)
    tensor = np.ascontiguousarray(rgb.transpose(2, 0, 1)[None], dtype=np.float32) / 255.0
    prediction = session.run(None, {session.get_inputs()[0].name: tensor})[0][0].T
    rows = prediction[prediction[:, 4] >= 0.25]
    if len(rows) == 0:
        return []
    x1 = rows[:, 0] - rows[:, 2] / 2
    y1 = rows[:, 1] - rows[:, 3] / 2
    rectangles = [[int(x), int(y), int(w), int(h)] for x, y, w, h in
                  zip(x1, y1, rows[:, 2], rows[:, 3])]
    selected = cv2.dnn.NMSBoxes(rectangles, rows[:, 4].tolist(), 0.25, 0.45)
    found = []
    for index in np.array(selected).reshape(-1)[:100]:
        row = rows[int(index)]
        left = max(0, int((row[0] - row[2] / 2 - pad_x) / scale))
        top = max(0, int((row[1] - row[3] / 2 - pad_y) / scale))
        right = min(width, int((row[0] + row[2] / 2 - pad_x) / scale))
        bottom = min(height, int((row[1] + row[3] / 2 - pad_y) / scale))
        if right - left < 2 or bottom - top < 2:
            continue
        found.append({"box": [left, top, right - left, bottom - top],
                      "score": float(row[4]), "class": "interactive_region"})
    return found


def main():
    model = os.environ.get("LINGDONGDAO_GUI_MODEL")
    if not model or not os.path.isfile(model):
        send({"ready": False, "error": "GUI ONNX model missing"})
        return
    options = ort.SessionOptions()
    options.intra_op_num_threads = 2
    options.inter_op_num_threads = 1
    session = ort.InferenceSession(model, sess_options=options,
                                   providers=["CPUExecutionProvider"])
    send({"ready": True, "class": "interactive_region", "pid": os.getpid()})
    for line in sys.stdin:
        request = {}
        try:
            request = json.loads(line)
            raw = base64.b64decode(request["jpeg"], validate=True)
            image = cv2.imdecode(np.frombuffer(raw, np.uint8), cv2.IMREAD_COLOR)
            if image is None:
                raise ValueError("invalid JPEG")
            send({"id": request["id"], "elements": detect(session, image)})
        except Exception as exc:
            send({"id": request.get("id"), "error": str(exc)[:300]})


if __name__ == "__main__":
    main()
