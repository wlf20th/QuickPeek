"""专测反斜杠/花括号转义的假服务（端口 18082）。

用 chr(92) 显式拼反斜杠，避免 Python 源码里的转义层级把人绕进去。
"""
import json
from http.server import BaseHTTPRequestHandler, HTTPServer

BS = chr(92)          # 一个反斜杠
FENCE = "~" * 3

BODY = (
    "## 反斜杠与转义\n\n"
    "行内代码：`a" + BS + "b" + BS * 2 + "c` —— 应显示 a" + BS + "b" + BS * 2 + "c\n\n"
    "普通文本：" + BS + " 和 " + BS * 2 + " 以及 " + BS * 3 + " 结束\n\n"
    + FENCE + "bash\n"
    "x = " + '"C:' + BS + "temp" + BS + "file.txt" + '"\n'
    "grep " + '"' + BS + BS + "d+" + '"' + " file\n"
    "echo ${HOME} | awk '{print $1}'\n"
    + FENCE + "\n\n"
    "| 表达式 | 含义 |\n"
    "| --- | --- |\n"
    "| `" + BS + "d+` | 数字 |\n"
    "| `{}` | 花括号 |\n"
)


class H(BaseHTTPRequestHandler):
    def do_POST(self):
        n = int(self.headers.get("Content-Length", 0))
        self.rfile.read(n)
        body = {
            "id": "mock",
            "object": "chat.completion",
            "model": "mock-bs",
            "choices": [{
                "index": 0,
                "message": {"role": "assistant", "content": BODY},
                "finish_reason": "stop",
            }],
            "usage": {"prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2},
        }
        data = json.dumps(body, ensure_ascii=False).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        data = b'{"data":[{"id":"mock-bs"}]}'
        self.send_response(200)
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def log_message(self, *a):
        pass


if __name__ == "__main__":
    HTTPServer(("127.0.0.1", 18082), H).serve_forever()
