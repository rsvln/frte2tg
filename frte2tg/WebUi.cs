namespace frte2tg
{
    internal static class WebUi
    {
        public static void Start(string configPath)
        {
            Task.Run(() =>
            {
                var builder = WebApplication.CreateBuilder();
                builder.WebHost.UseUrls("http://+:8888");
                builder.Logging.ClearProviders();
                var app = builder.Build();

                app.MapGet("/", () => Results.Content(GetHtml(), "text/html"));

                app.MapGet("/api/log", (int? lines) =>
                {
                    int n = lines ?? 200;
                    string logPath = "/var/log/frte2tg/frte2tg_"
                                   + DateTime.Now.AddMinutes(Program.settings.options.timeoffset)
                                              .ToString("yyyy-MM-dd") + ".log";
                    if (!File.Exists(logPath))
                        return Results.Ok(new { lines = Array.Empty<string>() });

                    var all = File.ReadAllLines(logPath);
                    var tail = all.Skip(Math.Max(0, all.Length - n)).ToArray();
                    return Results.Ok(new { lines = tail });
                });

                app.MapGet("/api/config", () =>
                {
                    if (!File.Exists(configPath))
                        return Results.NotFound();
                    return Results.Ok(new { content = File.ReadAllText(configPath) });
                });

                app.MapPost("/api/config", async (HttpRequest req) =>
                {
                    using var reader = new StreamReader(req.Body);
                    var body = await reader.ReadToEndAsync();
                    var data = System.Text.Json.JsonSerializer.Deserialize<ConfigPayload>(body);
                    if (data?.content == null)
                        return Results.BadRequest();

                    if (File.Exists(configPath))
                        File.Copy(configPath, configPath + ".bak", overwrite: true);

                    File.WriteAllText(configPath, data.content);
                    return Results.Ok(new { ok = true });
                });

                app.Run();
            });
        }

        record ConfigPayload(string content);

        private static string GetHtml() => """
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="UTF-8">
            <meta name="viewport" content="width=device-width, initial-scale=1.0">
            <title>frte2tg</title>
            <style>
              @import url('https://fonts.googleapis.com/css2?family=JetBrains+Mono:wght@400;600&family=IBM+Plex+Sans:wght@400;500&display=swap');

              :root {
                --bg: #0d1117;
                --bg2: #161b22;
                --bg3: #21262d;
                --border: #30363d;
                --text: #c9d1d9;
                --muted: #8b949e;
                --accent: #58a6ff;
                --green: #3fb950;
                --yellow: #d29922;
                --red: #f85149;
                --orange: #e3b341;
              }

              * { box-sizing: border-box; margin: 0; padding: 0; }

              body {
                background: var(--bg);
                color: var(--text);
                font-family: 'IBM Plex Sans', sans-serif;
                font-size: 14px;
                height: 100vh;
                display: flex;
                flex-direction: column;
                overflow: hidden;
              }

              header {
                background: var(--bg2);
                border-bottom: 1px solid var(--border);
                padding: 12px 20px;
                display: flex;
                align-items: center;
                gap: 16px;
                flex-shrink: 0;
              }

              header h1 {
                font-family: 'JetBrains Mono', monospace;
                font-size: 15px;
                color: var(--accent);
                letter-spacing: 0.05em;
              }

              header .dot {
                width: 8px; height: 8px;
                border-radius: 50%;
                background: var(--green);
                box-shadow: 0 0 6px var(--green);
                animation: pulse 2s infinite;
              }

              @keyframes pulse {
                0%, 100% { opacity: 1; }
                50% { opacity: 0.4; }
              }

              .tabs {
                display: flex;
                gap: 2px;
                margin-left: auto;
              }

              .tab {
                padding: 6px 16px;
                border-radius: 6px;
                border: 1px solid transparent;
                background: transparent;
                color: var(--muted);
                cursor: pointer;
                font-family: 'IBM Plex Sans', sans-serif;
                font-size: 13px;
                transition: all 0.15s;
              }

              .tab:hover { color: var(--text); background: var(--bg3); }
              .tab.active {
                background: var(--bg3);
                border-color: var(--border);
                color: var(--accent);
              }

              .panels { flex: 1; overflow: hidden; display: flex; flex-direction: column; }
              .panel { display: none; flex: 1; overflow: hidden; flex-direction: column; }
              .panel.active { display: flex; }


              .log-toolbar {
                padding: 10px 16px;
                background: var(--bg2);
                border-bottom: 1px solid var(--border);
                display: flex;
                align-items: center;
                gap: 12px;
                flex-shrink: 0;
              }

              .log-toolbar label { color: var(--muted); font-size: 12px; }

              .log-toolbar select, .log-toolbar input[type=number] {
                background: var(--bg3);
                border: 1px solid var(--border);
                color: var(--text);
                padding: 4px 8px;
                border-radius: 6px;
                font-size: 12px;
                font-family: 'IBM Plex Sans', sans-serif;
              }

              .btn {
                padding: 5px 14px;
                border-radius: 6px;
                border: 1px solid var(--border);
                background: var(--bg3);
                color: var(--text);
                cursor: pointer;
                font-size: 12px;
                font-family: 'IBM Plex Sans', sans-serif;
                transition: all 0.15s;
              }
              .btn:hover { border-color: var(--accent); color: var(--accent); }
              .btn.primary { background: var(--accent); border-color: var(--accent); color: #000; font-weight: 500; }
              .btn.primary:hover { opacity: 0.85; color: #000; }

              .autoscroll-toggle { margin-left: auto; display: flex; align-items: center; gap: 8px; }
              .autoscroll-toggle input { accent-color: var(--accent); }

              #log-container {
                  flex: 1;
                  overflow-y: auto;
                  overflow-x: auto;
                  padding: 12px 16px;
                  font-family: 'JetBrains Mono', monospace;
                  font-size: 12px;
                  line-height: 1.7;
              }

              #log-container::-webkit-scrollbar { width: 6px; }
              #log-container::-webkit-scrollbar-track { background: var(--bg); }
              #log-container::-webkit-scrollbar-thumb { background: var(--border); border-radius: 3px; }

              .log-line {
                  display: grid;
                  grid-template-columns: 190px 90px 220px 110px 1fr;
                  gap: 0 12px;
                  padding: 1px 0;
                  min-width: max-content;
                }
              .log-line:hover { filter: brightness(1.3); }

              .log-ts    { color: var(--muted); }
              .log-type  { }
              .log-type.app { color: var(--accent); }
              .log-type.tg { color: var(--accent); }
              .log-type.review { color: var(--green); }
              .log-type.event { color: var(--orange); }
              .log-id    { color: var(--muted); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
              .log-camera { color: var(--yellow); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
              .log-msg   { color: var(--text); white-space: nowrap; }
              .log-msg.error { color: var(--red); }

              .config-toolbar {
                padding: 10px 16px;
                background: var(--bg2);
                border-bottom: 1px solid var(--border);
                display: flex;
                align-items: center;
                gap: 12px;
                flex-shrink: 0;
              }

              .config-hint { color: var(--muted); font-size: 12px; margin-left: auto; }

              #config-editor {
                flex: 1;
                background: var(--bg);
                color: var(--text);
                border: none;
                outline: none;
                padding: 16px;
                font-family: 'JetBrains Mono', monospace;
                font-size: 13px;
                line-height: 1.7;
                resize: none;
                tab-size: 2;
              }

              .toast {
                position: fixed;
                bottom: 24px;
                right: 24px;
                padding: 10px 20px;
                border-radius: 8px;
                font-size: 13px;
                opacity: 0;
                transform: translateY(8px);
                transition: all 0.2s;
                pointer-events: none;
              }
              .toast.show { opacity: 1; transform: translateY(0); }
              .toast.ok { background: var(--green); color: #000; }
              .toast.err { background: var(--red); color: #fff; }
            </style>
            </head>
            <body>

            <header>
              <div class="dot"></div>
              <h1>Frigate TrueEnd Event to Telegram</h1>
              <div class="tabs">
                <button class="tab active" onclick="switchTab('log')">Log</button>
                <button class="tab" onclick="switchTab('config')">Config</button>
              </div>
            </header>

            <div class="panels">

              <div class="panel active" id="panel-log">
                <div class="log-toolbar">
                  <label>Lines:</label>
                  <input type="number" id="log-lines" value="200" min="10" max="2000" style="width:70px">
                  <button class="btn" onclick="loadLog()">Refresh</button>
                  <label>Type:</label>
                    <select id="filter-type" onchange="applyFilters()">
                      <option value="">All</option>
                      <option value="review">review</option>
                      <option value="event">event</option>
                      <option value="app">app</option>
                      <option value="ai">ai</option>
                      <option value="tg">tg</option>
                    </select>

                    <label>Camera:</label>
                    <input type="text" id="filter-camera" placeholder="e.g. dachacam09" 
                           oninput="applyFilters()" style="width:130px">

                    <label>Text:</label>
                    <input type="text" id="filter-text" placeholder="search..." 
                           oninput="applyFilters()" style="width:130px">

                    <button class="btn" onclick="clearFilters()">✕ Clear</button>

                  <div class="autoscroll-toggle">
                    <input type="checkbox" id="autoscroll" checked>
                    <label for="autoscroll">Auto-scroll</label>
                    <label style="margin-left:16px">Auto-refresh:</label>
                    <select id="refresh-interval" onchange="setRefresh()">
                      <option value="0">Off</option>
                      <option value="5000" selected>5s</option>
                      <option value="10000">10s</option>
                      <option value="30000">30s</option>
                    </select>
                  </div>
                </div>
                <div id="log-container"></div>
              </div>

              <div class="panel" id="panel-config">
                <div class="config-toolbar">
                  <button class="btn primary" onclick="saveConfig()">Save</button>
                  <button class="btn" onclick="loadConfig()">Reload</button>
                  <span class="config-hint">Backup (.bak) created automatically on save</span>
                </div>
                <textarea id="config-editor" spellcheck="false"></textarea>
              </div>

            </div>

            <div class="toast" id="toast"></div>

            <script>
            let refreshTimer = null;

            function switchTab(name) {
              document.querySelectorAll('.tab').forEach(t => t.classList.remove('active'));
              document.querySelectorAll('.panel').forEach(p => p.classList.remove('active'));
              event.target.classList.add('active');
              document.getElementById('panel-' + name).classList.add('active');
              if (name === 'config') loadConfig();
            }

            function setRefresh() {
              clearInterval(refreshTimer);
              const ms = parseInt(document.getElementById('refresh-interval').value);
              if (ms > 0) refreshTimer = setInterval(loadLog, ms);
            }

            let allLines = [];

            async function loadLog() {
              const n = document.getElementById('log-lines').value;
              const res = await fetch('/api/log?lines=' + n);
              const data = await res.json();
              allLines = data.lines;
              applyFilters();
            }

            function applyFilters() {
              const type = document.getElementById('filter-type').value.toLowerCase();
              const camera = document.getElementById('filter-camera').value.toLowerCase();
              const text = document.getElementById('filter-text').value.toLowerCase();

              const filtered = allLines.filter(line => {
                const parts = line.split('\t');
                if (parts.length < 5) return !type && !camera && !text;
                const [ts, t, id, cam, ...msgParts] = parts;
                const msg = msgParts.join('\t');
                if (type && !t.toLowerCase().includes(type)) return false;
                if (camera && !cam.toLowerCase().includes(camera)) return false;
                if (text && !msg.toLowerCase().includes(text) && !id.toLowerCase().includes(text)) return false;
                return true;
              });

              const container = document.getElementById('log-container');
              container.innerHTML = filtered.map(formatLine).join('');
              if (document.getElementById('autoscroll').checked)
                container.scrollTop = container.scrollHeight;
            }

            function clearFilters() {
              document.getElementById('filter-type').value = '';
              document.getElementById('filter-camera').value = '';
              document.getElementById('filter-text').value = '';
              applyFilters();
            }

            const EVENT_COLORS = [
              '#1a3a2a', '#2a1a3a', '#3a2a1a', '#1a2a3a', '#3a1a2a',
              '#1a3a3a', '#3a1a1a', '#2a3a1a', '#1a1a3a', '#3a3a1a',
              '#0d2a1a', '#2a0d1a', '#1a2a0d', '#0d1a2a', '#2a1a0d',
              '#0d2a2a', '#2a0d0d', '#1a0d2a', '#0d0d2a', '#2a2a0d',
              '#153020', '#201530', '#302015', '#152030', '#301520',
              '#153030', '#301515', '#203015', '#151530', '#303015',
            ];

            function idToColor(id) {
              if (!id) return 'transparent';
              let hash = 0;
              for (let i = 0; i < id.length; i++) {
                hash = ((hash << 5) - hash) + id.charCodeAt(i);
                hash |= 0;
              }
              return EVENT_COLORS[Math.abs(hash) % EVENT_COLORS.length];
            }

            function formatLine(line) {
              const parts = line.split('\t');
              if (parts.length < 5) return `<div class="log-line"><span class="log-msg">${esc(line)}</span></div>`;
              const [ts, type, id, camera, ...msgParts] = parts;
              const msg = msgParts.join('\t');
              const isError = msg.toLowerCase().includes('error');
              const bg = idToColor(id);
              return `<div class="log-line" style="background:${bg}; margin:0 -16px; padding:1px 16px;">
                <span class="log-ts">${esc(ts)}</span>
                <span class="log-type ${esc(type)}">${esc(type)}</span>
                <span class="log-id" title="${esc(id)}">${esc(id)}</span>
                <span class="log-camera">${esc(camera)}</span>
                <span class="log-msg${isError ? ' error' : ''}">${esc(msg)}</span>
              </div>`;
            }

            function esc(s) {
              return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;');
            }

            async function loadConfig() {
              const res = await fetch('/api/config');
              const data = await res.json();
              document.getElementById('config-editor').value = data.content;
            }

            async function saveConfig() {
              const content = document.getElementById('config-editor').value;
              const res = await fetch('/api/config', {
                method: 'POST',
                headers: {'Content-Type': 'application/json'},
                body: JSON.stringify({ content })
              });
              const data = await res.json();
              showToast(data.ok ? 'Saved' : 'Error', data.ok ? 'ok' : 'err');
            }

            function showToast(msg, type) {
              const t = document.getElementById('toast');
              t.textContent = msg;
              t.className = 'toast ' + type + ' show';
              setTimeout(() => t.classList.remove('show'), 2500);
            }


            loadLog();
            setRefresh();
            </script>
            </body>
            </html>
            """;
    }
}
