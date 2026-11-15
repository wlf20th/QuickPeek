"""返回一段富 Markdown 的假 OpenAI 服务，用来验证结果窗渲染。"""
import json
from http.server import BaseHTTPRequestHandler, HTTPServer

MD = """# 连接池超时排查结论

**结论**：连接池被打满，不是数据库宕机。当前 `maximumPoolSize=10` 已经耗尽。

## 关键证据

1. `HikariPool-1` 在 30s 内没有拿到连接
2. 活跃连接数 = 最大连接数（10/10）
3. 慢查询在同一时段堆积了 3 条

## 日志片段

```java
2026-11-14 13:00:01 ERROR HikariPool-1 - Connection is not available
com.zaxxer.hikari.pool.HikariPool$PoolEntryCreator
    request timed out after 30000ms
```

## 参数对照

| 参数 | 当前值 | 建议值 |
| --- | --- | --- |
| maximumPoolSize | 10 | 30 |
| connectionTimeout | 30000 | 5000 |
| leakDetectionThreshold | 0 | 20000 |

> 注意：先确认泄漏点，再调大连接数，否则只是把问题推后。

### 修复步骤

- 打开泄漏检测：`leakDetectionThreshold=20000`
- 抓 `SHOW PROCESSLIST` 里长时间 Sleep 的连接
- 把 *只读报表* 查询拆到独立数据源

---

*以上基于单条日志推断，需要结合 thread dump 复核。*
"""



SPECIAL = r"""## 转义测试

路径：`C:////Users////you////.dotnet//dotnet.exe`，反斜杠两个：\\

花括号 `{ config: { a: 1 } }`，美元符 $HOME，尖括号 <div class="x">&nbsp;</div>

**粗体里的 `代码`** 与 *斜体* 和 ~~删除线~~

~~~bash
echo "hello {world}"
cd C:////temp && ls -la
awk '{print $1}' file.txt
~~~

| 符号 | 说明 |
| --- | --- |
| `{}` | 花括号 |
| `<tag>` | 尖括号 |

> 引用里的 `代码` 和 **粗体**

emoji: ✅ ⏳ 🚀 中文标点：“引号”、《书名号》、——破折号
"""


class H(BaseHTTPRequestHandler):
    def do_POST(self):
        n = int(self.headers.get("Content-Length", 0))
        req = self.rfile.read(n)
        content = MD
        if b"long" in req:
            content = MD * 30
        elif b"special" in req:
            content = SPECIAL
        body = {
            "id": "mock",
            "object": "chat.completion",
            "model": "mock-md",
            "choices": [{
                "index": 0,
                "message": {"role": "assistant", "content": content},
                "finish_reason": "stop",
            }],
            "usage": {"prompt_tokens": 10, "completion_tokens": 200, "total_tokens": 210},
        }
        data = json.dumps(body, ensure_ascii=False).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        data = b'{"data":[{"id":"mock-md"}]}'
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def log_message(self, *a):
        pass


if __name__ == "__main__":
    HTTPServer(("127.0.0.1", 18081), H).serve_forever()
