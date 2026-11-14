"""假的 OpenAI 兼容服务，用于验证客户端解析（含 \\u 转义、reasoning_content）。"""
import json, time
from http.server import BaseHTTPRequestHandler, HTTPServer

REPLY = (
    "结论：日志里的错误是连接池耗尽。\n\n"
    "1. HikariPool-1 连接超时 30s，说明 10 个连接全被占住。\n"
    "2. 前一条 \"Connection is not available\" 是关键证据 —— 不是 SQL 慢，是泄漏。\n"
    "3. 建议把 maximumPoolSize 调到 20，同时加 leakDetectionThreshold=20000。\n\n"
    "特殊字符测试：<>&\"引号\"、反斜杠 C:\\data\\app、emoji ✅"
)


class H(BaseHTTPRequestHandler):
    def do_POST(self):
        length = int(self.headers.get("Content-Length", 0))
        body = self.rfile.read(length).decode("utf-8")
        print("REQ", self.path, "len", length, flush=True)
        try:
            payload = json.loads(body)
            print("MODEL", payload.get("model"), "msgs", len(payload.get("messages", [])), flush=True)
        except Exception as e:
            print("BAD JSON", e, flush=True)
            self.send_response(400)
            self.end_headers()
            return

        time.sleep(0.8)
        resp = {
            "id": "chatcmpl-mock",
            "object": "chat.completion",
            "created": int(time.time()),
            "model": payload.get("model", "mock"),
            "choices": [{
                "index": 0,
                "message": {
                    "role": "assistant",
                    "content": REPLY,
                    "reasoning_content": "先看时间戳，再看异常栈…"
                },
                "finish_reason": "stop"
            }],
            "usage": {"prompt_tokens": 42, "completion_tokens": 88, "total_tokens": 130}
        }
        data = json.dumps(resp, ensure_ascii=True).encode()  # 故意用 \u 转义
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def log_message(self, *a):
        pass


if __name__ == "__main__":
    HTTPServer(("127.0.0.1", 18080), H).serve_forever()
