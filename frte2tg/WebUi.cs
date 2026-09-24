using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

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

                app.MapGet("/", () => Results.Content(Localize(GetHtml()), "text/html; charset=utf-8"));

                app.MapGet("/api/log", (int? lines) =>
                {
                    int n = lines ?? 200;
                    string logDir = "/var/log/frte2tg/";

                    var logFiles = Directory.GetFiles(logDir, "frte2tg_*.log")
                                            .OrderByDescending(f => f)
                                            .ToArray();

                    if (logFiles.Length == 0)
                        return Results.Ok(new { lines = Array.Empty<string>() });

                    var result = new List<string>();
                    foreach (var file in logFiles)
                    {
                        if (result.Count >= n) break;
                        var fileLines = File.ReadAllLines(file);
                        result.InsertRange(0, fileLines);
                    }

                    var tail = result.Skip(Math.Max(0, result.Count - n)).ToArray();
                    return Results.Ok(new { lines = tail });
                });

                app.MapGet("/api/meta", () => Safe(() => Results.Ok(StatsService.GetMeta())));

                app.MapGet("/api/last", (string camera, string label, int? limit) => Safe(() =>
                {
                    camera = string.IsNullOrEmpty(camera) ? null : camera;
                    label = string.IsNullOrEmpty(label) ? null : label;
                    // Without a camera: last `limit` events of every camera; with a camera: last `limit` of that camera.
                    var rows = camera == null
                        ? StatsService.GetLastPerCamera(Math.Clamp(limit ?? 1, 1, 50), label)
                        : StatsService.GetLast(camera, label, Math.Clamp(limit ?? 24, 1, 200));
                    return Results.Ok(rows.Select(r => new
                    {
                        r.id, r.camera, r.label, r.sub_label, r.score, r.start_time, r.end_time, r.zones, r.has_snapshot, r.has_clip,
                        start_local = StatsService.ToLocal(r.start_time).ToString("yyyy-MM-dd HH:mm:ss")
                    }));
                }));

                // label: empty = all objects, "config" = what the bot is configured to send, otherwise a single label.
                app.MapGet("/api/stat", (string period, string camera, string label) => Safe(() =>
                {
                    bool configOnly = label == "config";
                    return Results.Ok(StatsService.GetStats(period,
                                                            string.IsNullOrEmpty(camera) ? null : camera,
                                                            string.IsNullOrEmpty(label) || configOnly ? null : label,
                                                            configOnly));
                }));

                // The id is looked up in Frigate's DB, so only real snapshot files can be served.
                app.MapGet("/api/snapshot/{id}", async (string id) =>
                {
                    try
                    {
                        var ev = StatsService.GetEvent(id);
                        var bytes = ev == null ? null : await StatsService.GetSnapshotAsync(ev);
                        return bytes == null ? Results.NotFound() : Results.File(bytes, "image/jpeg");
                    }
                    catch (Exception ex) { return Results.Problem(ex.Message); }
                });

                // Event clip built from Frigate's recording segments (up to now for events in progress); ?download=1 sends it as an attachment.
                app.MapGet("/api/clip/{id}", async (string id, int? download) =>
                {
                    try
                    {
                        var ev = StatsService.GetEvent(id);
                        string path = ev == null ? null : await StatsService.GetClipPathAsync(ev);
                        if (path == null)
                            return Results.NotFound();
                        return download == 1
                            ? Results.File(path, "video/mp4", ev.camera + "-" + ev.id + ".mp4", enableRangeProcessing: true)
                            : Results.File(path, "video/mp4", enableRangeProcessing: true);
                    }
                    catch (Exception ex) { return Results.Problem(ex.Message); }
                });

                // Version and the README rendered to HTML for the About tab.
                app.MapGet("/api/about", () => Safe(() =>
                {
                    string readmePath = Path.Combine(Program.appLocation, "README.md");
                    string readme = File.Exists(readmePath)
                        ? Markdig.Markdown.ToHtml(File.ReadAllText(readmePath), Markdig.MarkdownExtensions.UseAdvancedExtensions(new Markdig.MarkdownPipelineBuilder()).Build())
                        : "";
                    return Results.Ok(new { version = VersionInfo.Version, build = VersionInfo.BuildDate, url = VersionInfo.ProjectUrl, readme });
                }));

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

                    try
                    {
                        Program.settings = (new DeserializerBuilder()
                            .WithNamingConvention(UnderscoredNamingConvention.Instance)
                            .Build())
                            .Deserialize<SettingsFile>(File.ReadAllText(configPath));
                        await Program.Initialize();
                        Program.Log("app", "", "", "Settings reloaded");
                    }
                    catch (Exception ex)
                    {
                        Program.Log("app", "", "", "Failed to reload settings: " + ex.Message);
                    }

                    return Results.Ok(new { ok = true });
                });

                app.Run();
            });
        }

        record ConfigPayload(string content);

        // Fills {{key}} placeholders in the page and hands web./label. strings to its scripts as I18N.
        static string Localize(string html)
        {
            html = System.Text.RegularExpressions.Regex.Replace(html, @"\{\{([\w.]+)\}\}", m => System.Net.WebUtility.HtmlEncode(L10n.T(m.Groups[1].Value)));
            html = html.Replace("%VERSION%", System.Net.WebUtility.HtmlEncode(VersionInfo.Version))
                       .Replace("%BUILD%", System.Net.WebUtility.HtmlEncode(VersionInfo.BuildDate))
                       .Replace("%URL%", VersionInfo.ProjectUrl);
            return html.Replace("/*I18N*/{}", System.Text.Json.JsonSerializer.Serialize(L10n.Export("web.", "label.")));
        }

        static IResult Safe(Func<IResult> f)
        {
            try { return f(); }
            catch (Exception ex) { return Results.Problem(ex.Message); }
        }

        private static string GetHtml() => """
            <!DOCTYPE html>
            <html lang="{{web.lang}}">
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
              .log-type.fr { color: #bc8cff; }
              .log-type.ai { color: #ff7b72; }
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

              .log-toolbar input[type=text] {
                background: var(--bg3);
                border: 1px solid var(--border);
                color: var(--text);
                padding: 4px 8px;
                border-radius: 6px;
                font-size: 12px;
              }

              .scroll { flex: 1; overflow-y: auto; padding: 16px; }
              .scroll::-webkit-scrollbar { width: 6px; }
              .scroll::-webkit-scrollbar-thumb { background: var(--border); border-radius: 3px; }
              .empty { color: var(--muted); padding: 24px 0; text-align: center; }

              .cards { display: grid; grid-template-columns: repeat(auto-fill, minmax(260px, 1fr)); gap: 12px; }
              .card {
                background: var(--bg2);
                border: 1px solid var(--border);
                border-radius: 8px;
                overflow: hidden;
                display: flex;
                flex-direction: column;
              }
              .card .img {
                aspect-ratio: 16 / 9;
                background: var(--bg3);
                display: flex; align-items: center; justify-content: center;
                color: var(--muted); font-size: 12px;
              }
              .card img { width: 100%; height: 100%; object-fit: cover; cursor: zoom-in; display: block; }
              .card .meta { padding: 8px 10px; display: flex; flex-direction: column; gap: 3px; font-size: 12px; }
              .card .row1 { display: flex; justify-content: space-between; gap: 8px; }
              .card .cam { color: var(--yellow); font-family: 'JetBrains Mono', monospace; }
              .card .lbl { color: var(--text); font-weight: 500; font-size: 13px; }
              .card .sub { color: #bc8cff; }
              .card .score { color: var(--green); font-family: 'JetBrains Mono', monospace; }
              .card .when, .card .zones { color: var(--muted); }
              .card .live { color: var(--red); font-family: 'IBM Plex Sans', sans-serif; font-size: 11px; }

              .lightbox {
                position: fixed; inset: 0;
                background: rgba(0,0,0,0.85);
                display: none; align-items: center; justify-content: center;
                cursor: zoom-out; z-index: 10;
              }
              .lightbox.show { display: flex; }
              .lightbox img, .lightbox video { max-width: 95vw; max-height: 95vh; border-radius: 6px; }
              .lightbox video { background: #000; cursor: default; }
              .card .actions { display: flex; justify-content: flex-end; gap: 6px; margin-top: 4px; }
              .card .act {
                padding: 2px 10px;
                border-radius: 6px;
                border: 1px solid var(--border);
                background: var(--bg3);
                color: var(--text);
                font-size: 12px;
                font-family: 'IBM Plex Sans', sans-serif;
                cursor: pointer;
                text-decoration: none;
              }
              .card .act:hover { border-color: var(--accent); color: var(--accent); }

              .kpis { display: grid; grid-template-columns: repeat(auto-fill, minmax(170px, 1fr)); gap: 12px; margin-bottom: 20px; }
              .kpi { background: var(--bg2); border: 1px solid var(--border); border-radius: 8px; padding: 12px 14px; }
              .kpi .k { color: var(--muted); font-size: 12px; }
              .kpi .v { font-family: 'JetBrains Mono', monospace; font-size: 22px; font-weight: 600; margin-top: 4px; }
              .kpi .v.small { font-size: 15px; padding-top: 5px; }

              .section { margin-bottom: 24px; }
              .section h3 { font-size: 13px; font-weight: 500; color: var(--muted); margin-bottom: 8px; }

              table.matrix { border-collapse: collapse; font-family: 'JetBrains Mono', monospace; font-size: 12px; }
              table.matrix th, table.matrix td { border: 1px solid var(--border); padding: 5px 10px; text-align: right; }
              table.matrix th { background: var(--bg2); color: var(--muted); font-weight: 400; }
              table.matrix th:first-child, table.matrix td:first-child { text-align: left; color: var(--yellow); }
              table.matrix tr.total td { background: var(--bg2); font-weight: 600; }
              table.matrix td.zero { color: var(--border); }
              table.matrix td.clickable { cursor: pointer; }
              table.matrix td.clickable:hover { outline: 1px solid var(--accent); }

              .bars { display: flex; align-items: flex-end; gap: 3px; height: 140px; padding-top: 16px; }
              .bar { flex: 1; display: flex; flex-direction: column; align-items: center; justify-content: flex-end; height: 100%; min-width: 0; }
              .bar .fill { width: 100%; background: var(--accent); border-radius: 3px 3px 0 0; min-height: 1px; opacity: 0.85; }
              .bar .fill.peak { background: var(--orange); }
              .bar .n { font-size: 10px; color: var(--muted); margin-bottom: 2px; font-family: 'JetBrains Mono', monospace; }
              .bar-axis { display: flex; gap: 3px; margin-top: 4px; }
              .bar-axis span { flex: 1; text-align: center; font-size: 10px; color: var(--muted); font-family: 'JetBrains Mono', monospace; min-width: 0; overflow: hidden; }

              .app-footer {
                flex-shrink: 0;
                display: flex;
                gap: 16px;
                align-items: center;
                padding: 6px 20px;
                background: var(--bg2);
                border-top: 1px solid var(--border);
                color: var(--muted);
                font-size: 12px;
              }
              .app-footer b { color: var(--text); font-weight: 500; }
              .app-footer a, .about-meta a { color: var(--accent); text-decoration: none; }
              .app-footer a:hover, .about-meta a:hover { text-decoration: underline; }

              .about-head { margin-bottom: 20px; padding-bottom: 14px; border-bottom: 1px solid var(--border); }
              .about-name { font-family: 'JetBrains Mono', monospace; font-size: 22px; color: var(--accent); }
              .about-meta { display: flex; flex-wrap: wrap; gap: 18px; margin-top: 6px; color: var(--muted); font-size: 13px; }
              .about-meta b { color: var(--text); font-weight: 500; }

              .markdown { max-width: 980px; line-height: 1.6; }
              .markdown h1 { font-size: 22px; margin: 18px 0 10px; }
              .markdown h2 { font-size: 18px; margin: 24px 0 10px; padding-bottom: 4px; border-bottom: 1px solid var(--border); }
              .markdown h3 { font-size: 15px; margin: 18px 0 8px; }
              .markdown p, .markdown ul, .markdown ol, .markdown table, .markdown pre { margin: 0 0 12px; }
              .markdown ul, .markdown ol { padding-left: 24px; }
              .markdown a { color: var(--accent); }
              .markdown code { font-family: 'JetBrains Mono', monospace; font-size: 12px; background: var(--bg3); padding: 1px 5px; border-radius: 4px; }
              .markdown pre { background: var(--bg2); border: 1px solid var(--border); border-radius: 6px; padding: 12px; overflow-x: auto; }
              .markdown pre code { background: none; padding: 0; }
              .markdown table { border-collapse: collapse; display: block; overflow-x: auto; }
              .markdown th, .markdown td { border: 1px solid var(--border); padding: 5px 10px; text-align: left; vertical-align: top; }
              .markdown th { background: var(--bg2); }

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
              <h1>Frigate TrueEnd Events and Reviews to Telegram</h1>
              <div class="tabs">
                <button class="tab active" onclick="switchTab('log')">{{web.tab.log}}</button>
                <button class="tab" onclick="switchTab('last')">{{web.tab.last}}</button>
                <button class="tab" onclick="switchTab('stats')">{{web.tab.stats}}</button>
                <button class="tab" onclick="switchTab('config')">{{web.tab.config}}</button>
                <button class="tab" onclick="switchTab('about')">{{web.tab.about}}</button>
              </div>
            </header>

            <div class="panels">

              <div class="panel active" id="panel-log">
                <div class="log-toolbar">
                  <label>{{web.lines}}</label>
                  <input type="number" id="log-lines" value="200" min="10" max="2000" style="width:70px">
                  <button class="btn" onclick="loadLog()">{{web.refresh}}</button>
                  <label>{{web.type}}</label>
                    <select id="filter-type" onchange="applyFilters()">
                      <option value="">{{web.all}}</option>
                      <option value="review">review</option>
                      <option value="event">event</option>
                      <option value="app">app</option>
                      <option value="ai">ai</option>
                      <option value="tg">tg</option>
                    </select>

                    <label>{{web.camera}}</label>
                    <input type="text" id="filter-camera" placeholder="{{web.camera_placeholder}}" 
                           oninput="applyFilters()" style="width:130px">

                    <label>{{web.text}}</label>
                    <input type="text" id="filter-text" placeholder="{{web.search_placeholder}}" 
                           oninput="applyFilters()" style="width:130px">

                    <button class="btn" onclick="clearFilters()">{{web.clear}}</button>

                  <div class="autoscroll-toggle">
                    <input type="checkbox" id="autoscroll" checked>
                    <label for="autoscroll">{{web.autoscroll}}</label>
                    <label style="margin-left:16px">{{web.autorefresh}}</label>
                    <select id="refresh-interval" onchange="setRefresh()">
                      <option value="0">{{web.off}}</option>
                      <option value="5000" selected>5s</option>
                      <option value="10000">10s</option>
                      <option value="30000">30s</option>
                    </select>
                  </div>
                </div>
                <div id="log-container"></div>
              </div>

              <div class="panel" id="panel-last">
                <div class="log-toolbar">
                  <label>{{web.camera}}</label>
                  <select id="last-camera" class="meta-camera" onchange="lastCameraChanged()"><option value="">{{web.all}}</option></select>
                  <label>{{web.object}}</label>
                  <select id="last-label" class="meta-label" onchange="loadLast()"><option value="">{{web.all}}</option></select>
                  <label id="last-limit-label">{{web.per_camera}}</label>
                  <select id="last-limit" onchange="loadLast()">
                    <option value="1" selected>1</option>
                    <option value="3">3</option>
                    <option value="5">5</option>
                    <option value="10">10</option>
                    <option value="20">20</option>
                    <option value="50">50</option>
                  </select>
                  <button class="btn" onclick="loadLast()">{{web.refresh}}</button>
                  <div class="autoscroll-toggle">
                    <label>{{web.autorefresh}}</label>
                    <select id="last-refresh" onchange="setLastRefresh()">
                      <option value="0">{{web.off}}</option>
                      <option value="10000">10s</option>
                      <option value="30000" selected>30s</option>
                      <option value="60000">60s</option>
                    </select>
                  </div>
                </div>
                <div class="scroll"><div id="last-cards"></div></div>
              </div>

              <div class="panel" id="panel-stats">
                <div class="log-toolbar">
                  <label>{{web.period}}</label>
                  <select id="stat-period" onchange="loadStats()">
                    <option value="24h" selected>{{web.period.24h}}</option>
                    <option value="today">{{web.period.today}}</option>
                    <option value="7d">{{web.period.7d}}</option>
                    <option value="30d">{{web.period.30d}}</option>
                  </select>
                  <label>{{web.camera}}</label>
                  <select id="stat-camera" class="meta-camera" onchange="loadStats()"><option value="">{{web.all}}</option></select>
                  <label>{{web.object}}</label>
                  <select id="stat-label" class="meta-label" onchange="loadStats()"><option value="config">{{web.filter_config}}</option><option value="">{{web.all}}</option></select>
                  <button class="btn" onclick="loadStats()">{{web.refresh}}</button>
                </div>
                <div class="scroll" id="stats-body"></div>
              </div>

              <div class="panel" id="panel-config">
                <div class="config-toolbar">
                  <button class="btn primary" onclick="saveConfig()">{{web.config.save}}</button>
                  <button class="btn" onclick="loadConfig()">{{web.config.reload}}</button>
                  <span class="config-hint">{{web.config.hint}}</span>
                </div>
                <textarea id="config-editor" spellcheck="false"></textarea>
              </div>

              <div class="panel" id="panel-about">
                <div class="scroll">
                  <div class="about-head">
                    <div class="about-name">frte2tg</div>
                    <div class="about-meta">
                      <span>{{web.about.version}} <b>%VERSION%</b></span>
                      <span>{{web.about.build}} <b>%BUILD%</b></span>
                      <a href="%URL%" target="_blank" rel="noopener">GitHub</a>
                      <a href="%URL%/releases" target="_blank" rel="noopener">{{web.about.changes}}</a>
                      <span>MIT</span>
                    </div>
                  </div>
                  <div class="markdown" id="about-readme"></div>
                </div>
              </div>

            </div>

            <footer class="app-footer">
              <span>frte2tg <b>v%VERSION%</b></span>
              <span>{{web.about.build}} %BUILD%</span>
              <a href="%URL%" target="_blank" rel="noopener">GitHub</a>
            </footer>

            <div class="toast" id="toast"></div>
            <div class="lightbox" id="lightbox" onclick="closeLightbox(event)"><img id="lightbox-img" alt=""><video id="lightbox-video" controls playsinline></video></div>

            <script>
            let refreshTimer = null;

            function switchTab(name) {
              document.querySelectorAll('.tab').forEach(t => t.classList.remove('active'));
              document.querySelectorAll('.panel').forEach(p => p.classList.remove('active'));
              event.target.classList.add('active');
              document.getElementById('panel-' + name).classList.add('active');
              if (name === 'config') loadConfig();
              clearInterval(lastTimer);
              if (name === 'last') { loadMeta().then(loadLast); setLastRefresh(); }
              if (name === 'stats') loadMeta().then(loadStats);
              if (name === 'about') loadAbout();
            }

            const I18N = /*I18N*/{};
            const t = (k, ...a) => (I18N[k] ?? k).replace(/\{(\d+)\}/g, (_, i) => a[i]);
            const EMOJI = { person: '👤', car: '🚗', dog: '🐕', cat: '🐈', bird: '🐦' };
            const labelName = l => (EMOJI[l] ? EMOJI[l] + ' ' : '') + (I18N['label.' + l] || l);

            let metaLoaded = false;
            async function loadMeta() {
              if (metaLoaded) return;
              const res = await fetch('/api/meta');
              if (!res.ok) return;
              const meta = await res.json();
              const fill = (cls, items, fmt) => document.querySelectorAll(cls).forEach(sel => {
                sel.innerHTML = '<option value="">{{web.all}}</option>' +
                  items.map(v => `<option value="${esc(v)}">${esc(fmt(v))}</option>`).join('');
              });
              fill('.meta-camera', meta.cameras, v => v);
              fill('.meta-label', meta.labels, labelName);
              // Stats default to what the bot is configured to send.
              const statLabel = document.getElementById('stat-label');
              statLabel.insertAdjacentHTML('afterbegin', '<option value="config">{{web.filter_config}}</option>');
              statLabel.value = 'config';
              metaLoaded = true;
            }

            function ago(unix) {
              const s = Math.max(0, Date.now() / 1000 - unix);
              if (s < 60) return t('web.just_now');
              if (s < 3600) return t('web.min_ago', Math.floor(s / 60));
              if (s < 86400) return t('web.h_ago', Math.floor(s / 3600));
              return t('web.d_ago', Math.floor(s / 86400));
            }

            let lastTimer = null;
            function setLastRefresh() {
              clearInterval(lastTimer);
              const ms = parseInt(document.getElementById('last-refresh').value);
              if (ms > 0) lastTimer = setInterval(loadLast, ms);
            }

            // One camera: show its history (20 by default); all cameras: N latest of each (1 by default).
            function lastCameraChanged() {
              const cam = document.getElementById('last-camera').value;
              document.getElementById('last-limit-label').textContent = cam ? t('web.events_limit') : t('web.per_camera');
              document.getElementById('last-limit').value = cam ? '20' : '1';
              loadLast();
            }

            async function loadLast() {
              const p = new URLSearchParams();
              const cam = document.getElementById('last-camera').value;
              const lbl = document.getElementById('last-label').value;
              const lim = document.getElementById('last-limit').value;
              if (cam) p.set('camera', cam);
              if (lbl) p.set('label', lbl);
              p.set('limit', lim);
              const box = document.getElementById('last-cards');
              const res = await fetch('/api/last?' + p);
              if (!res.ok) { box.innerHTML = `<div class="empty">${esc(t('web.error_events'))}</div>`; return; }
              const rows = await res.json();
              if (!rows.length) { box.innerHTML = `<div class="empty">${esc(t('web.no_events'))}</div>`; return; }

              if (!cam && lim !== '1') {
                const groups = [];
                rows.forEach(r => {
                  if (!groups.length || groups[groups.length - 1].camera !== r.camera) groups.push({ camera: r.camera, rows: [] });
                  groups[groups.length - 1].rows.push(r);
                });
                box.innerHTML = groups.map(g => `<div class="section"><h3>📷 ${esc(g.camera)}</h3>
                  <div class="cards">${g.rows.map(eventCard).join('')}</div></div>`).join('');
              } else {
                box.innerHTML = `<div class="cards">${rows.map(eventCard).join('')}</div>`;
              }
            }

            function eventCard(r) {
              return `
                <div class="card">
                  <div class="img">${r.has_snapshot
                    ? `<img loading="lazy" src="/api/snapshot/${encodeURIComponent(r.id)}" onclick="openLightbox(this.src)" onerror="this.replaceWith(t('web.no_snapshot'))" alt="">`
                    : esc(t('web.no_snapshot'))}</div>
                  <div class="meta">
                    <div class="row1"><span class="lbl">${esc(labelName(r.label))}${r.sub_label ? ` <span class="sub">(${esc(r.sub_label)})</span>` : ''}</span>
                      <span class="score">${Math.round(r.score * 100)}%</span></div>
                    <div class="row1"><span class="cam">${esc(r.camera)}${r.end_time === null ? ` <span class="live">● ${esc(t('web.in_progress'))}</span>` : ''}</span>
                      <span class="when" title="${esc(r.start_local)}">${esc(r.start_local.slice(5, 16))} · ${ago(r.start_time)}</span></div>
                    ${r.zones.length ? `<div class="zones">${esc(r.zones.join(', '))}</div>` : ''}
                    <div class="actions">
                      <button class="act" data-id="${esc(r.id)}" onclick="openVideo(this.dataset.id)">▶ ${esc(t('web.video'))}</button>
                      <a class="act" href="/api/clip/${encodeURIComponent(r.id)}?download=1" title="${esc(t('web.download'))}">⬇</a>
                    </div>
                  </div>
                </div>`;
            }

            let aboutLoaded = false;
            async function loadAbout() {
              if (aboutLoaded) return;
              const res = await fetch('/api/about');
              if (!res.ok) return;
              const data = await res.json();
              document.getElementById('about-readme').innerHTML = data.readme;
              document.querySelectorAll('#about-readme a[href^="http"]').forEach(a => { a.target = '_blank'; a.rel = 'noopener'; });
              aboutLoaded = true;
            }

            function openLightbox(src) {
              const img = document.getElementById('lightbox-img');
              img.src = src;
              img.style.display = '';
              document.getElementById('lightbox-video').style.display = 'none';
              document.getElementById('lightbox').classList.add('show');
            }

            function openVideo(id) {
              const video = document.getElementById('lightbox-video');
              document.getElementById('lightbox-img').style.display = 'none';
              video.style.display = '';
              video.onerror = () => { closeLightbox(); showToast(t('web.video_error'), 'err'); };
              video.src = '/api/clip/' + encodeURIComponent(id);
              document.getElementById('lightbox').classList.add('show');
              video.play().catch(() => {});
            }

            // Closes on a click outside the video, so its controls stay usable.
            function closeLightbox(e) {
              if (e && e.target.id === 'lightbox-video') return;
              const video = document.getElementById('lightbox-video');
              video.onerror = null;
              video.pause();
              video.removeAttribute('src');
              video.load();
              document.getElementById('lightbox').classList.remove('show');
            }

            function barChart(values, labels, peakIdx) {
              const max = Math.max(1, ...values);
              return `<div class="bars">${values.map((v, i) => `
                  <div class="bar" title="${esc(labels[i])}: ${v}">
                    <span class="n">${v || ''}</span>
                    <div class="fill${i === peakIdx ? ' peak' : ''}" style="height:${v ? Math.max(2, v / max * 100) : 0}%"></div>
                  </div>`).join('')}</div>
                <div class="bar-axis">${labels.map(l => `<span>${esc(l)}</span>`).join('')}</div>`;
            }

            function filterStats(camera, label) {
              if (camera !== undefined) document.getElementById('stat-camera').value = camera;
              if (label !== undefined) document.getElementById('stat-label').value = label;
              loadStats();
            }

            async function loadStats() {
              const p = new URLSearchParams({ period: document.getElementById('stat-period').value });
              const cam = document.getElementById('stat-camera').value;
              const lbl = document.getElementById('stat-label').value;
              if (cam) p.set('camera', cam);
              if (lbl) p.set('label', lbl);
              const body = document.getElementById('stats-body');
              const res = await fetch('/api/stat?' + p);
              if (!res.ok) { body.innerHTML = `<div class="empty">${esc(t('web.error_stats'))}</div>`; return; }
              const st = await res.json();

              const topCam = st.cameras[0];
              let html = `<div class="kpis">
                <div class="kpi"><div class="k">${t('web.kpi.events')}</div><div class="v">${st.total}</div></div>
                <div class="kpi"><div class="k">${t('web.kpi.alerts')}</div><div class="v" style="color:var(--red)">${st.alerts}</div></div>
                <div class="kpi"><div class="k">${t('web.kpi.detections')}</div><div class="v" style="color:var(--yellow)">${st.detections}</div></div>
                <div class="kpi"><div class="k">${t('web.kpi.busiest')}</div><div class="v small">${topCam ? esc(topCam) + ' · ' + Object.values(st.matrix[topCam]).reduce((a, b) => a + b, 0) : '—'}</div></div>
                <div class="kpi"><div class="k">${t('web.kpi.peak')}</div><div class="v small">${st.peakHour >= 0 ? String(st.peakHour).padStart(2, '0') + ':00–' + String((st.peakHour + 1) % 24).padStart(2, '0') + ':00' : '—'}</div></div>
              </div>`;

              if (!st.total) { body.innerHTML = html + `<div class="empty">${esc(t('web.no_events_period'))}</div>`; return; }

              const max = Math.max(...st.cameras.flatMap(c => Object.values(st.matrix[c])));
              const cell = (v, c, l) => v
                ? `<td class="clickable" data-c="${esc(c)}" data-l="${esc(l)}" onclick="filterStats(this.dataset.c, this.dataset.l)" style="background:rgba(88,166,255,${(0.08 + 0.5 * v / max).toFixed(2)})">${v}</td>`
                : `<td class="zero">·</td>`;
              html += `<div class="section"><h3>${t('web.matrix.title')}</h3><div style="overflow-x:auto"><table class="matrix">
                <tr><th>${t('web.matrix.camera')}</th>${st.labels.map(l => `<th>${esc(labelName(l))}</th>`).join('')}<th>${t('web.matrix.total')}</th><th>${t('web.matrix.last_event')}</th></tr>
                ${st.cameras.map(c => {
                  const row = st.matrix[c];
                  const sum = Object.values(row).reduce((a, b) => a + b, 0);
                  const last = st.lastByCamera[c];
                  return `<tr><td class="clickable" data-c="${esc(c)}" onclick="filterStats(this.dataset.c)">${esc(c)}</td>${st.labels.map(l => cell(row[l] || 0, c, l)).join('')}
                    <td>${sum}</td><td style="color:var(--muted)">${last ? ago(last) : ''}</td></tr>`;
                }).join('')}
                <tr class="total"><td>${t('web.matrix.total')}</td>${st.labels.map(l => `<td>${st.labelTotals[l]}</td>`).join('')}<td>${st.total}</td><td></td></tr>
              </table></div></div>`;

              html += `<div class="section"><h3>${t('web.by_hour')}</h3>${barChart(st.hours, st.hours.map((_, i) => String(i)), st.peakHour)}</div>`;
              if (st.days.length > 2)
                html += `<div class="section"><h3>${t('web.by_day')}</h3>${barChart(st.days.map(d => d.count), st.days.map(d => d.day.slice(8) + '.' + d.day.slice(5, 7)), -1)}</div>`;

              body.innerHTML = html;
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
              return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
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
              showToast(data.ok ? t('web.config.saved') : t('web.config.error'), data.ok ? 'ok' : 'err');
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
