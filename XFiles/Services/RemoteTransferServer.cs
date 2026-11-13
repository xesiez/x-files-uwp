using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking.Connectivity;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using XFiles.FileSystem;

namespace XFiles.Services
{
    public sealed class RemoteTransferServer : IDisposable
    {
        private StreamSocketListener _listener;
        private string _targetDirectory;
        private int _port = 8088;
        private bool _isRunning;
        private CancellationTokenSource _cts;

        public bool IsRunning => _isRunning;
        public int Port => _port;
        public string TargetDirectory => _targetDirectory;

        public event Action<string> OnClientConnected;
        public event Action<string, string> OnDownloadRequested; // url, fileName
        public event Action<string, long, long> OnUploadProgress; // fileName, bytesReceived, totalBytes
        public event Action<string, string> OnFileUploaded; // fileName, savedPath
        public event Action<string> OnStatusMessage;

        public static string GetLocalIpAddress()
        {
            try
            {
                var hosts = NetworkInformation.GetHostNames();
                foreach (var host in hosts)
                {
                    if (host.IPInformation != null &&
                        host.Type == Windows.Networking.HostNameType.Ipv4 &&
                        !host.CanonicalName.StartsWith("127.") &&
                        !host.CanonicalName.StartsWith("169.254."))
                    {
                        return host.CanonicalName;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("RemoteTransferServer.GetLocalIpAddress failed: {Error}", ex.Message);
            }
            return "127.0.0.1";
        }

        public string GetServerUrl()
        {
            return $"http://{GetLocalIpAddress()}:{_port}";
        }

        public async Task<bool> StartAsync(string targetDirectory, int port = 8088)
        {
            if (_isRunning) Stop();

            _targetDirectory = targetDirectory;
            _port = port;
            _cts = new CancellationTokenSource();

            try
            {
                _listener = new StreamSocketListener();
                _listener.Control.KeepAlive = true;
                _listener.ConnectionReceived += OnConnectionReceived;

                await _listener.BindServiceNameAsync(_port.ToString());
                _isRunning = true;
                Log.Info("RemoteTransferServer started on port {Port} targeting {Dir}", _port, _targetDirectory);
                OnStatusMessage?.Invoke($"Server active on {GetServerUrl()}");
                return true;
            }
            catch (Exception ex)
            {
                Log.Err("RemoteTransferServer.StartAsync failed: {Error}", ex, ex.Message);
                _isRunning = false;
                _listener?.Dispose();
                _listener = null;
                return false;
            }
        }

        public void Stop()
        {
            if (!_isRunning) return;
            _isRunning = false;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;

            try
            {
                _listener?.Dispose();
                _listener = null;
            }
            catch (Exception ex)
            {
                Log.Warn("RemoteTransferServer.Stop error: {Error}", ex.Message);
            }

            Log.Info("RemoteTransferServer stopped");
            OnStatusMessage?.Invoke("Server stopped");
        }

        private async void OnConnectionReceived(StreamSocketListener sender, StreamSocketListenerConnectionReceivedEventArgs args)
        {
            if (!_isRunning) return;
            string remoteIp = args.Socket.Information.RemoteAddress?.CanonicalName ?? "unknown";
            OnClientConnected?.Invoke(remoteIp);

            try
            {
                using (var socket = args.Socket)
                using (var inStream = socket.InputStream)
                using (var outStream = socket.OutputStream)
                using (var netStream = inStream.AsStreamForRead(16384))
                using (var writer = new StreamWriter(outStream.AsStreamForWrite(16384), Encoding.UTF8))
                {
                    await HandleHttpRequestAsync(netStream, writer, remoteIp);
                }
            }
            catch (Exception ex)
            {
                Log.Dbg("RemoteTransferServer connection handler ended: {Msg}", ex.Message);
            }
        }

        private async Task HandleHttpRequestAsync(Stream stream, StreamWriter writer, string remoteIp)
        {
            // Read request line and headers
            var headerBytes = new MemoryStream();
            int b;
            int consecutiveNewlines = 0;
            while ((b = stream.ReadByte()) != -1)
            {
                headerBytes.WriteByte((byte)b);
                if (b == '\n')
                {
                    consecutiveNewlines++;
                    if (headerBytes.Length >= 4)
                    {
                        byte[] arr = headerBytes.ToArray();
                        int len = arr.Length;
                        if (arr[len - 4] == '\r' && arr[len - 3] == '\n' && arr[len - 2] == '\r' && arr[len - 1] == '\n')
                            break;
                    }
                }
                if (headerBytes.Length > 32768) break; // header limit
            }

            string headerStr = Encoding.UTF8.GetString(headerBytes.ToArray());
            var lines = headerStr.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            if (lines.Length == 0 || string.IsNullOrWhiteSpace(lines[0])) return;

            var reqParts = lines[0].Split(' ');
            if (reqParts.Length < 2) return;
            string method = reqParts[0].ToUpperInvariant();
            string path = reqParts[1];

            long contentLength = 0;
            string contentType = "";
            string customFileName = "";

            foreach (var line in lines)
            {
                int colon = line.IndexOf(':');
                if (colon > 0)
                {
                    string hName = line.Substring(0, colon).Trim().ToLowerInvariant();
                    string hVal = line.Substring(colon + 1).Trim();
                    if (hName == "content-length" && long.TryParse(hVal, out long cl))
                        contentLength = cl;
                    else if (hName == "content-type")
                        contentType = hVal;
                    else if (hName == "x-file-name")
                        customFileName = WebUtility.UrlDecode(hVal);
                }
            }

            if (method == "GET" && (path == "/" || path.StartsWith("/?")))
            {
                string html = BuildWebHtml();
                byte[] htmlBytes = Encoding.UTF8.GetBytes(html);
                await writer.WriteAsync($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {htmlBytes.Length}\r\nConnection: close\r\n\r\n");
                await writer.FlushAsync();
                await writer.BaseStream.WriteAsync(htmlBytes, 0, htmlBytes.Length);
                await writer.BaseStream.FlushAsync();
                return;
            }

            if (method == "POST" && path.StartsWith("/api/download"))
            {
                // Read JSON or form body
                byte[] bodyBytes = new byte[(int)Math.Min(contentLength, 65536)];
                int totalRead = 0;
                while (totalRead < bodyBytes.Length)
                {
                    int r = await stream.ReadAsync(bodyBytes, totalRead, bodyBytes.Length - totalRead);
                    if (r <= 0) break;
                    totalRead += r;
                }
                string bodyStr = Encoding.UTF8.GetString(bodyBytes, 0, totalRead);
                string downloadUrl = ExtractJsonOrFormParam(bodyStr, "url");

                if (!string.IsNullOrWhiteSpace(downloadUrl))
                {
                    OnStatusMessage?.Invoke($"Received URL: {downloadUrl}");
                    OnDownloadRequested?.Invoke(downloadUrl, _targetDirectory);
                    string resp = "{\"status\":\"ok\",\"message\":\"Download initiated on Xbox!\"}";
                    byte[] respBytes = Encoding.UTF8.GetBytes(resp);
                    await writer.WriteAsync($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {respBytes.Length}\r\nConnection: close\r\n\r\n");
                    await writer.FlushAsync();
                    await writer.BaseStream.WriteAsync(respBytes, 0, respBytes.Length);
                    await writer.BaseStream.FlushAsync();
                }
                else
                {
                    string resp = "{\"status\":\"error\",\"message\":\"Missing URL\"}";
                    byte[] respBytes = Encoding.UTF8.GetBytes(resp);
                    await writer.WriteAsync($"HTTP/1.1 400 Bad Request\r\nContent-Type: application/json\r\nContent-Length: {respBytes.Length}\r\nConnection: close\r\n\r\n");
                    await writer.FlushAsync();
                    await writer.BaseStream.WriteAsync(respBytes, 0, respBytes.Length);
                    await writer.BaseStream.FlushAsync();
                }
                return;
            }

            if (method == "POST" && path.StartsWith("/api/upload"))
            {
                await HandleFileUploadAsync(stream, writer, contentType, contentLength, customFileName);
                return;
            }

            // 404
            string notFound = "{\"status\":\"not_found\"}";
            byte[] nfBytes = Encoding.UTF8.GetBytes(notFound);
            await writer.WriteAsync($"HTTP/1.1 404 Not Found\r\nContent-Type: application/json\r\nContent-Length: {nfBytes.Length}\r\nConnection: close\r\n\r\n");
            await writer.FlushAsync();
            await writer.BaseStream.WriteAsync(nfBytes, 0, nfBytes.Length);
            await writer.BaseStream.FlushAsync();
        }

        private async Task HandleFileUploadAsync(Stream stream, StreamWriter writer, string contentType, long contentLength, string customFileName)
        {
            try
            {
                string fileName = !string.IsNullOrWhiteSpace(customFileName) ? customFileName : "upload_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".bin";

                if (contentType.StartsWith("multipart/form-data"))
                {
                    // Parse boundary
                    string boundary = "";
                    int bIdx = contentType.IndexOf("boundary=");
                    if (bIdx > 0) boundary = "--" + contentType.Substring(bIdx + 9).Trim();

                    // Read boundary header
                    var subHead = new MemoryStream();
                    int b;
                    while ((b = stream.ReadByte()) != -1)
                    {
                        subHead.WriteByte((byte)b);
                        if (subHead.Length >= 4)
                        {
                            byte[] arr = subHead.ToArray();
                            int l = arr.Length;
                            if (arr[l - 4] == '\r' && arr[l - 3] == '\n' && arr[l - 2] == '\r' && arr[l - 1] == '\n')
                                break;
                        }
                        if (subHead.Length > 8192) break;
                    }

                    string partHead = Encoding.UTF8.GetString(subHead.ToArray());
                    int fnIdx = partHead.IndexOf("filename=\"");
                    if (fnIdx > 0)
                    {
                        int fnEnd = partHead.IndexOf("\"", fnIdx + 10);
                        if (fnEnd > fnIdx)
                            fileName = Path.GetFileName(partHead.Substring(fnIdx + 10, fnEnd - (fnIdx + 10)));
                    }

                    // Compute payload length roughly
                    long bytesRemaining = contentLength > 0 ? (contentLength - subHead.Length - (boundary.Length + 6)) : 0;
                    string destPath = Path.Combine(_targetDirectory, fileName);
                    OnStatusMessage?.Invoke($"Receiving {fileName}...");

                    using (var fs = Win32FileWriteStream.Create(destPath))
                    {
                        byte[] buf = new byte[32768];
                        long received = 0;
                        int toRead = (int)Math.Min(buf.Length, bytesRemaining > 0 ? bytesRemaining : buf.Length);
                        int r;
                        while (toRead > 0 && (r = await stream.ReadAsync(buf, 0, toRead)) > 0)
                        {
                            fs.Write(buf, 0, r);
                            received += r;
                            OnUploadProgress?.Invoke(fileName, received, bytesRemaining > 0 ? bytesRemaining : received);
                            if (bytesRemaining > 0)
                                toRead = (int)Math.Min(buf.Length, bytesRemaining - received);
                        }
                    }

                    OnFileUploaded?.Invoke(fileName, destPath);
                    OnStatusMessage?.Invoke($"✓ Uploaded {fileName} successfully!");

                    string resp = $"{{\"status\":\"ok\",\"fileName\":\"{WebUtility.HtmlEncode(fileName)}\"}}";
                    byte[] respBytes = Encoding.UTF8.GetBytes(resp);
                    await writer.WriteAsync($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {respBytes.Length}\r\nConnection: close\r\n\r\n");
                    await writer.FlushAsync();
                    await writer.BaseStream.WriteAsync(respBytes, 0, respBytes.Length);
                    await writer.BaseStream.FlushAsync();
                    return;
                }
                else
                {
                    // Raw stream upload
                    string destPath = Path.Combine(_targetDirectory, fileName);
                    OnStatusMessage?.Invoke($"Receiving {fileName}...");

                    using (var fs = Win32FileWriteStream.Create(destPath))
                    {
                        byte[] buf = new byte[32768];
                        long received = 0;
                        int r;
                        while ((r = await stream.ReadAsync(buf, 0, buf.Length)) > 0)
                        {
                            fs.Write(buf, 0, r);
                            received += r;
                            OnUploadProgress?.Invoke(fileName, received, contentLength > 0 ? contentLength : received);
                            if (contentLength > 0 && received >= contentLength) break;
                        }
                    }

                    OnFileUploaded?.Invoke(fileName, destPath);
                    OnStatusMessage?.Invoke($"✓ Uploaded {fileName} successfully!");

                    string resp = $"{{\"status\":\"ok\",\"fileName\":\"{WebUtility.HtmlEncode(fileName)}\"}}";
                    byte[] respBytes = Encoding.UTF8.GetBytes(resp);
                    await writer.WriteAsync($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {respBytes.Length}\r\nConnection: close\r\n\r\n");
                    await writer.FlushAsync();
                    await writer.BaseStream.WriteAsync(respBytes, 0, respBytes.Length);
                    await writer.BaseStream.FlushAsync();
                }
            }
            catch (Exception ex)
            {
                Log.Err("RemoteTransferServer upload failed: {Error}", ex, ex.Message);
                string resp = $"{{\"status\":\"error\",\"message\":\"{WebUtility.HtmlEncode(ex.Message)}\"}}";
                byte[] respBytes = Encoding.UTF8.GetBytes(resp);
                await writer.WriteAsync($"HTTP/1.1 500 Internal Server Error\r\nContent-Type: application/json\r\nContent-Length: {respBytes.Length}\r\nConnection: close\r\n\r\n");
                await writer.FlushAsync();
                await writer.BaseStream.WriteAsync(respBytes, 0, respBytes.Length);
                await writer.BaseStream.FlushAsync();
            }
        }

        private static string ExtractJsonOrFormParam(string body, string paramName)
        {
            if (string.IsNullOrWhiteSpace(body)) return null;

            // JSON: "url": "..."
            int keyIdx = body.IndexOf($"\"{paramName}\"");
            if (keyIdx >= 0)
            {
                int colon = body.IndexOf(':', keyIdx);
                if (colon > 0)
                {
                    int valStart = body.IndexOf('\"', colon);
                    if (valStart > 0)
                    {
                        int valEnd = body.IndexOf('\"', valStart + 1);
                        if (valEnd > valStart)
                            return body.Substring(valStart + 1, valEnd - valStart - 1).Trim();
                    }
                }
            }

            // Form: url=...
            var pairs = body.Split('&');
            foreach (var p in pairs)
            {
                var kv = p.Split('=');
                if (kv.Length == 2 && kv[0].Trim() == paramName)
                    return WebUtility.UrlDecode(kv[1].Trim());
            }

            return body.Trim();
        }

        private string BuildWebHtml()
        {
            string dirName = WebUtility.HtmlEncode(Path.GetFileName(_targetDirectory) ?? _targetDirectory);
            return $@"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1.0, maximum-scale=1.0, user-scalable=no"">
<title>X-Filesplus Remote Hub</title>
<style>
  :root {{
    --bg:#0A0C10; --card:#141820; --border:#252D3A; --accent:#93C43C;
    --accent-glow:rgba(147,196,60,0.3); --text:#F0F2F5; --text-dim:#8A92A0;
    --purple:#DF00FF; --input-bg:#0D1117;
  }}
  * {{ box-sizing:border-box; margin:0; padding:0; font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif; }}
  body {{ background:var(--bg); color:var(--text); padding:16px; min-height:100vh; display:flex; flex-direction:column; align-items:center; }}
  .container {{ width:100%; max-width:480px; }}
  header {{ text-align:center; padding:12px 0 20px; }}
  .logo {{ font-size:24px; font-weight:800; color:var(--accent); letter-spacing:1px; }}
  .dest-badge {{ display:inline-block; background:#1C2418; border:1px solid #365022; color:#B4E26A; font-size:12px; padding:4px 10px; border-radius:12px; margin-top:8px; word-break:break-all; }}
  .card {{ background:var(--card); border:1px solid var(--border); border-radius:14px; padding:18px; margin-bottom:18px; box-shadow:0 4px 20px rgba(0,0,0,0.4); }}
  .card-title {{ font-size:15px; font-weight:700; color:var(--text); margin-bottom:12px; display:flex; align-items:center; gap:8px; }}
  input[type=""text""], input[type=""url""] {{
    width:100%; background:var(--input-bg); border:1px solid var(--border);
    color:#FFF; padding:12px 14px; border-radius:8px; font-size:14px; outline:none; transition:border 0.2s;
  }}
  input[type=""text""]:focus, input[type=""url""]:focus {{ border-color:var(--accent); box-shadow:0 0 0 2px var(--accent-glow); }}
  .btn {{
    display:block; width:100%; background:var(--accent); color:#0A1604; font-weight:700;
    border:none; padding:12px 16px; border-radius:8px; font-size:15px; margin-top:12px; cursor:pointer;
    transition:background 0.2s, transform 0.1s; text-align:center;
  }}
  .btn:active {{ transform:scale(0.98); }}
  .file-drop {{
    border:2px dashed var(--border); border-radius:10px; padding:24px 16px; text-align:center;
    background:var(--input-bg); cursor:pointer; transition:border-color 0.2s;
  }}
  .file-drop:hover {{ border-color:var(--accent); }}
  .status-box {{ padding:12px; border-radius:8px; font-size:13px; margin-top:12px; display:none; line-height:1.4; }}
  .status-ok {{ background:#142910; color:#85E868; border:1px solid #28541D; }}
  .status-err {{ background:#330E14; color:#FF7B88; border:1px solid #661A24; }}
  .status-prog {{ background:#141B26; color:#70A4FF; border:1px solid #233754; }}
  .progress-bar {{ width:100%; height:6px; background:#222; border-radius:3px; overflow:hidden; margin-top:8px; display:none; }}
  .progress-fill {{ height:100%; width:0%; background:var(--accent); transition:width 0.1s; }}
</style>
</head>
<body>
<div class=""container"">
  <header>
    <div class=""logo"">🎮 X-Filesplus</div>
    <div class=""dest-badge"">📁 Folder: {dirName}</div>
  </header>

  <!-- Section 1: Send Download Link -->
  <div class=""card"">
    <div class=""card-title"">📥 Send Download URL to Xbox</div>
    <input type=""url"" id=""urlInput"" placeholder=""Paste download link here (Direct, Drive, etc.)"" autofocus autocomplete=""off"">
    <button class=""btn"" id=""sendUrlBtn"" onclick=""sendDownloadUrl()"">🚀 Download on Xbox</button>
    <div id=""urlStatus"" class=""status-box""></div>
  </div>

  <!-- Section 2: Upload File to Xbox -->
  <div class=""card"">
    <div class=""card-title"">📤 Upload File directly from Phone</div>
    <div class=""file-drop"" onclick=""document.getElementById('fileInput').click()"">
      <div style=""font-size:28px; margin-bottom:6px;"">📁</div>
      <div style=""font-weight:600; font-size:14px;"">Tap to choose a file</div>
      <div style=""color:var(--text-dim); font-size:12px; margin-top:4px;"">Photos, ROMs, ZIPs, Videos, Music</div>
    </div>
    <input type=""file"" id=""fileInput"" style=""display:none;"" onchange=""uploadSelectedFile()"">
    <div class=""progress-bar"" id=""uploadProgBar""><div class=""progress-fill"" id=""uploadProgFill""></div></div>
    <div id=""uploadStatus"" class=""status-box""></div>
  </div>
</div>

<script>
async function sendDownloadUrl() {{
  const inp = document.getElementById('urlInput');
  const btn = document.getElementById('sendUrlBtn');
  const st = document.getElementById('urlStatus');
  const url = inp.value.trim();

  if (!url) {{
    showStatus(st, 'Please paste a valid URL', 'err');
    return;
  }}

  btn.disabled = true;
  btn.innerText = 'Sending...';
  showStatus(st, 'Sending to Xbox...', 'prog');

  try {{
    const res = await fetch('/api/download', {{
      method: 'POST',
      headers: {{ 'Content-Type': 'application/json' }},
      body: JSON.stringify({{ url: url }})
    }});
    const data = await res.json();
    if (res.ok && data.status === 'ok') {{
      showStatus(st, '✓ Download started on Xbox!', 'ok');
      inp.value = '';
    }} else {{
      showStatus(st, 'Failed: ' + (data.message || 'Error'), 'err');
    }}
  }} catch (e) {{
    showStatus(st, 'Error connecting to Xbox: ' + e.message, 'err');
  }} finally {{
    btn.disabled = false;
    btn.innerText = '🚀 Download on Xbox';
  }}
}}

async function uploadSelectedFile() {{
  const fi = document.getElementById('fileInput');
  if (!fi.files || !fi.files[0]) return;
  const file = fi.files[0];

  const st = document.getElementById('uploadStatus');
  const bar = document.getElementById('uploadProgBar');
  const fill = document.getElementById('uploadProgFill');

  bar.style.display = 'block';
  fill.style.width = '0%';
  showStatus(st, 'Uploading ' + file.name + ' (' + Math.round(file.size / 1024) + ' KB)...', 'prog');

  const xhr = new XMLHttpRequest();
  xhr.open('POST', '/api/upload');
  xhr.setRequestHeader('X-File-Name', encodeURIComponent(file.name));

  xhr.upload.onprogress = function(e) {{
    if (e.lengthComputable) {{
      const pct = Math.round((e.loaded / e.total) * 100);
      fill.style.width = pct + '%';
      st.innerText = 'Uploading ' + file.name + ': ' + pct + '%';
    }}
  }};

  xhr.onload = function() {{
    bar.style.display = 'none';
    if (xhr.status === 200) {{
      showStatus(st, '✓ ' + file.name + ' saved to Xbox!', 'ok');
    }} else {{
      showStatus(st, 'Upload failed: HTTP ' + xhr.status, 'err');
    }}
    fi.value = '';
  }};

  xhr.onerror = function() {{
    bar.style.display = 'none';
    showStatus(st, 'Upload connection error', 'err');
    fi.value = '';
  }};

  xhr.send(file);
}}

function showStatus(elem, msg, type) {{
  elem.style.display = 'block';
  elem.className = 'status-box status-' + type;
  elem.innerText = msg;
}}
</script>
</body>
</html>";
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
