from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import os
import webbrowser

ROOT = Path(__file__).resolve().parent
os.chdir(ROOT)

class Handler(SimpleHTTPRequestHandler):
    def end_headers(self):
        self.send_header('Cache-Control', 'no-store')
        super().end_headers()

if __name__ == '__main__':
    address = ('127.0.0.1', 8765)
    try:
        server = ThreadingHTTPServer(address, Handler)
    except OSError:
        print('8765 포트를 사용할 수 없습니다. 이미 실행 중이면 http://127.0.0.1:8765 를 여세요.')
        raise SystemExit(1)
    print('타브만든댕: http://127.0.0.1:8765 (종료: Ctrl+C)')
    webbrowser.open('http://127.0.0.1:8765')
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()
