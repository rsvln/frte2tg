using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using System.Text.RegularExpressions;

namespace frte2tg
{
    // Read-only queries against Frigate's `event` / `reviewsegment` tables for /last, /stat and the web UI.
    // All user-supplied values go through SQL parameters.
    public static class StatsService
    {
        const string ScoreExpr = "COALESCE(top_score, json_extract(data, '$.top_score'), json_extract(data, '$.score'), 0)";
        const string NotFalsePositive = "(false_positive IS NULL OR false_positive = 0)";

        static SqliteConnection Open()
        {
            var db = new SqliteConnection("Data Source = " + Program.settings.frigate.dbpath);
            db.Open();
            return db;
        }

        public static DateTime ToLocal(double unix) =>
            DateTime.UnixEpoch.AddSeconds(unix).AddMinutes(Program.settings.options.timeoffset);

        static DateTime LocalNow => DateTime.UtcNow.AddMinutes(Program.settings.options.timeoffset);

        public static string SnapshotPath(string camera, string id)
        {
            string path = Program.settings.frigate.clipspath + "/" + camera + "-" + id + ".jpg";
            return System.IO.File.Exists(path) ? path : null;
        }

        static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        // Frigate writes {camera}-{id}.jpg only when an event ends; for an event still in progress
        // the current best frame is taken from Frigate's API. Returns null if neither is available.
        public static async Task<byte[]> GetSnapshotAsync(EventRow ev)
        {
            string path = SnapshotPath(ev.camera, ev.id);
            if (path != null)
                return await System.IO.File.ReadAllBytesAsync(path);
            if (ev.end_time != null)
                return null;
            return await GetFrigateSnapshotAsync(ev.id);
        }

        // Current best frame of an event from Frigate's HTTP API, or null if Frigate doesn't have one.
        public static async Task<byte[]> GetFrigateSnapshotAsync(string eventId)
        {
            try
            {
                var f = Program.settings.frigate;
                return await http.GetByteArrayAsync("http://" + f.host + ":" + f.port + "/api/events/" + Uri.EscapeDataString(eventId) + "/snapshot.jpg");
            }
            catch
            {
                return null;
            }
        }

        static readonly TimeSpan LiveClipTtl = TimeSpan.FromSeconds(30);
        static string ClipCacheDir => Path.Combine(Program.appLocation, "clipcache");
        static readonly HttpClient clipHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        // Event clip for the web UI, cached on disk so the browser can seek in it.
        // Built the way the bot builds clips: Frigate's recording segments covering the event, joined by ffmpeg
        // (Frigate's own clip.mp4 is unreliable when streams are out of sync). Frigate's clip is used only when no
        // segment is on disk. Returns null if nothing is available.
        public static async Task<string> GetClipPathAsync(EventRow ev)
        {
            Directory.CreateDirectory(ClipCacheDir);
            foreach (var old in Directory.GetFiles(ClipCacheDir).Where(f => System.IO.File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddHours(-1)))
                try { System.IO.File.Delete(old); } catch { }

            // An event still in progress gets the recording up to now; that clip is rebuilt when older than
            // LiveClipTtl, but reused meanwhile, since the player sends several range requests for one playback.
            bool live = ev.end_time == null;
            string path = Path.Combine(ClipCacheDir, ev.camera + "-" + ev.id + (live ? "-live" : "") + ".mp4");
            if (System.IO.File.Exists(path) && (!live || System.IO.File.GetLastWriteTimeUtc(path) > DateTime.UtcNow - LiveClipTtl))
                return path;

            string tmp = Path.Combine(ClipCacheDir, Guid.NewGuid().ToString("N") + ".mp4");
            var segments = GetRecordingSegments(ev);
            if (segments.Count > 0 && await ConcatSegmentsAsync(segments, tmp))
            {
                System.IO.File.Move(tmp, path, overwrite: true);
                return path;
            }
            try { System.IO.File.Delete(tmp); } catch { }

            if (live || !ev.has_clip)
                return null;
            var f = Program.settings.frigate;
            using var response = await clipHttp.GetAsync("http://" + f.host + ":" + f.port + "/api/events/" + Uri.EscapeDataString(ev.id) + "/clip.mp4",
                                                         HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
                return null;
            using (var fs = System.IO.File.Create(tmp))
                await response.Content.CopyToAsync(fs);
            System.IO.File.Move(tmp, path, overwrite: true);
            return path;
        }

        // Local paths of the camera's recording segments overlapping the event, in order.
        static List<string> GetRecordingSegments(EventRow ev)
        {
            var f = Program.settings.frigate;
            var result = new List<string>();
            using var db = Open();
            using var cmd = new SqliteCommand("SELECT path FROM recordings WHERE camera = $camera AND end_time > $start AND start_time < $end ORDER BY start_time", db);
            cmd.Parameters.AddWithValue("$camera", ev.camera);
            cmd.Parameters.AddWithValue("$start", ev.start_time);
            cmd.Parameters.AddWithValue("$end", ev.end_time ?? (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds);
            using var dr = cmd.ExecuteReader();
            while (dr.Read())
            {
                string real = dr.GetString(0).Replace(f.recordingsoriginalpath, f.recordingspath);
                if (System.IO.File.Exists(real))
                    result.Add(real);
            }
            return result;
        }

        static async Task<bool> ConcatSegmentsAsync(List<string> segments, string mp4Path)
        {
            string listPath = mp4Path + ".txt";
            await System.IO.File.WriteAllLinesAsync(listPath, segments.Select(p => "file '" + p.Replace("'", "'\\''") + "'"));
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("ffmpeg") { UseShellExecute = false, RedirectStandardError = true };
                foreach (var a in new[] { "-y", "-hide_banner", "-loglevel", "error", "-f", "concat", "-safe", "0", "-i", listPath,
                                          "-c", "copy", "-movflags", "+faststart", mp4Path })
                    psi.ArgumentList.Add(a);
                using var p = System.Diagnostics.Process.Start(psi);
                string err = await p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync();
                if (p.ExitCode != 0)
                    Program.Log("app", "", "", "ffmpeg failed to build web clip: " + err.Trim());
                return p.ExitCode == 0 && System.IO.File.Exists(mp4Path) && new FileInfo(mp4Path).Length > 0;
            }
            catch (Exception ex)
            {
                Program.Log("app", "", "", "ffmpeg failed to build web clip: " + ex.Message);
                return false;
            }
            finally
            {
                try { System.IO.File.Delete(listPath); } catch { }
            }
        }

        public static MetaResult GetMeta()
        {
            var cameras = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var labels = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in Program.settings.frigate.cameras ?? new List<Camera>())
                cameras.Add(c.camera);

            using var db = Open();
            using (var cmd = new SqliteCommand("SELECT DISTINCT camera FROM event WHERE start_time >= $from", db))
            {
                cmd.Parameters.AddWithValue("$from", Unix(DateTime.UtcNow.AddDays(-30)));
                using var dr = cmd.ExecuteReader();
                while (dr.Read()) cameras.Add(dr.GetString(0));
            }
            using (var cmd = new SqliteCommand("SELECT DISTINCT label FROM event", db))
            {
                using var dr = cmd.ExecuteReader();
                while (dr.Read()) labels.Add(dr.GetString(0));
            }
            return new MetaResult { cameras = cameras.ToList(), labels = labels.ToList() };
        }

        // Parses free-form command arguments: camera names, labels (English or Russian alias),
        // a number (limit) and a period (24h, 7d, today). Anything unrecognized goes to `unknown`.
        public static CommandFilter ParseArgs(IEnumerable<string> args)
        {
            var f = new CommandFilter();
            MetaResult meta = null;
            foreach (var raw in args)
            {
                string a = raw.Trim().ToLower();
                if (a.Length == 0) continue;

                if (int.TryParse(a, out int n)) { f.limit = n; continue; }
                if (TryParsePeriod(a, out _, out _)) { f.period = a; continue; }

                meta ??= GetMeta();
                string cam = meta.cameras.FirstOrDefault(c => c.Equals(a, StringComparison.OrdinalIgnoreCase));
                if (cam != null) { f.camera = cam; continue; }

                string label = meta.labels.FirstOrDefault(l => l.Equals(a, StringComparison.OrdinalIgnoreCase))
                               ?? L10n.LabelFromName(a);
                if (label != null) { f.label = label; continue; }

                f.unknown.Add(raw);
            }
            return f;
        }

        public static bool TryParsePeriod(string period, out DateTime fromUtc, out string title)
        {
            fromUtc = DateTime.UtcNow.AddHours(-24);
            title = L10n.T("period.hours", 24);
            if (string.IsNullOrEmpty(period)) return true;

            period = period.ToLower();
            if (period == "today" || period == "сегодня" || period == L10n.T("period.today").ToLower())
            {
                fromUtc = LocalNow.Date.AddMinutes(-Program.settings.options.timeoffset);
                title = L10n.T("period.today");
                return true;
            }
            var m = Regex.Match(period, @"^(\d{1,3})([hdчд])$");
            if (!m.Success) return false;
            int n = int.Parse(m.Groups[1].Value);
            if (n <= 0) return false;
            bool hours = m.Groups[2].Value is "h" or "ч";
            if (hours) { fromUtc = DateTime.UtcNow.AddHours(-n); title = L10n.T("period.hours", n); }
            else { fromUtc = DateTime.UtcNow.AddDays(-n); title = L10n.T("period.days", n); }
            return true;
        }

        public static List<EventRow> GetLast(string camera, string label, int limit)
        {
            string sql = "SELECT id, camera, label, sub_label, " + ScoreExpr + " AS score, start_time, end_time, zones, has_snapshot, has_clip " +
                         "FROM event WHERE " + NotFalsePositive +
                         (camera != null ? " AND camera = $camera" : "") +
                         (label != null ? " AND label = $label" : "") +
                         " ORDER BY start_time DESC LIMIT $limit";
            using var db = Open();
            using var cmd = new SqliteCommand(sql, db);
            if (camera != null) cmd.Parameters.AddWithValue("$camera", camera);
            if (label != null) cmd.Parameters.AddWithValue("$label", label);
            cmd.Parameters.AddWithValue("$limit", limit);
            return ReadEvents(cmd);
        }

        // Latest `perCamera` events of every camera (optionally of one label only), grouped by camera:
        // cameras ordered by their most recent event, events newest first.
        public static List<EventRow> GetLastPerCamera(int perCamera = 1, string label = null)
        {
            // Frigate's event table has its own (legacy, usually 0) `score` column, so the computed one gets another name inside.
            string sql = "SELECT id, camera, label, sub_label, ev_score AS score, start_time, end_time, zones, has_snapshot, has_clip FROM (" +
                         "  SELECT *, " + ScoreExpr + " AS ev_score, " +
                         "         ROW_NUMBER() OVER (PARTITION BY camera ORDER BY start_time DESC) AS rn, " +
                         "         MAX(start_time) OVER (PARTITION BY camera) AS cam_last " +
                         "  FROM event WHERE " + NotFalsePositive + " AND start_time >= $from" +
                         (label != null ? " AND label = $label" : "") +
                         ") WHERE rn <= $n ORDER BY cam_last DESC, camera, start_time DESC";
            using var db = Open();
            using var cmd = new SqliteCommand(sql, db);
            cmd.Parameters.AddWithValue("$from", Unix(DateTime.UtcNow.AddDays(-30)));
            cmd.Parameters.AddWithValue("$n", perCamera);
            if (label != null) cmd.Parameters.AddWithValue("$label", label);
            return ReadEvents(cmd);
        }

        public static EventRow GetEvent(string id)
        {
            string sql = "SELECT id, camera, label, sub_label, " + ScoreExpr + " AS score, start_time, end_time, zones, has_snapshot, has_clip " +
                         "FROM event WHERE id = $id";
            using var db = Open();
            using var cmd = new SqliteCommand(sql, db);
            cmd.Parameters.AddWithValue("$id", id);
            return ReadEvents(cmd).FirstOrDefault();
        }

        static List<EventRow> ReadEvents(SqliteCommand cmd)
        {
            var result = new List<EventRow>();
            using var dr = cmd.ExecuteReader();
            while (dr.Read())
            {
                var row = new EventRow
                {
                    id = dr["id"].ToString(),
                    camera = dr["camera"].ToString(),
                    label = dr["label"].ToString(),
                    sub_label = dr["sub_label"] is DBNull ? null : dr["sub_label"].ToString(),
                    score = dr["score"] is DBNull ? 0 : Convert.ToDouble(dr["score"]),
                    start_time = Convert.ToDouble(dr["start_time"]),
                    end_time = dr["end_time"] is DBNull ? null : Convert.ToDouble(dr["end_time"]),
                    zones = ParseZones(dr["zones"]),
                    has_clip = !(dr["has_clip"] is DBNull) && Convert.ToInt64(dr["has_clip"]) != 0,
                };
                // In-progress events get their snapshot from the Frigate API, see GetSnapshotAsync.
                row.has_snapshot = row.end_time == null || SnapshotPath(row.camera, row.id) != null;
                if (string.IsNullOrWhiteSpace(row.sub_label)) row.sub_label = null;
                result.Add(row);
            }
            return result;
        }

        static List<string> ParseZones(object v)
        {
            if (v is DBNull || v == null) return new List<string>();
            try { return JsonConvert.DeserializeObject<List<string>>(v.ToString()) ?? new List<string>(); }
            catch { return new List<string>(); }
        }

        // Same rule the bot uses to decide what to send: the camera must be listed in frigate.cameras,
        // and its `objects` list (if not empty) must contain the label.
        public static bool ConfigAllows(string camera, string label = null, double? score = null)
        {
            var cam = Program.settings.frigate.cameras?.FirstOrDefault(c => c.camera == camera);
            if (cam == null) return false;
            if (label == null) return true;
            if (score != null) return Program.ObjectPasses(cam, label, score.Value);
            return cam.objects == null || cam.objects.Count == 0 || cam.objects.Any(o => o.label == label);
        }

        // `configOnly` limits the stats to cameras and objects the bot is configured to send.
        public static StatsResult GetStats(string period, string camera, string label, bool configOnly = false)
        {
            if (!TryParsePeriod(period, out DateTime fromUtc, out string title))
                TryParsePeriod(null, out fromUtc, out title);

            var st = new StatsResult { period = title, from = Unix(fromUtc), to = Unix(DateTime.UtcNow), camera = camera, label = label, configOnly = configOnly };
            var cameras = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var labels = new Dictionary<string, int>();
            var days = new SortedDictionary<string, int>();

            // Pre-fill every day of the period so gaps show up as zeros.
            DateTime fromLocal = ToLocal(st.from).Date;
            for (DateTime d = fromLocal; d <= LocalNow.Date; d = d.AddDays(1))
                days[d.ToString("yyyy-MM-dd")] = 0;

            using var db = Open();
            string where = " WHERE start_time >= $from" +
                           (camera != null ? " AND camera = $camera" : "");

            using (var cmd = new SqliteCommand("SELECT camera, label, start_time, " + ScoreExpr + " AS ev_score FROM event" + where + " AND " + NotFalsePositive +
                                               (label != null ? " AND label = $label" : ""), db))
            {
                cmd.Parameters.AddWithValue("$from", st.from);
                if (camera != null) cmd.Parameters.AddWithValue("$camera", camera);
                if (label != null) cmd.Parameters.AddWithValue("$label", label);
                using var dr = cmd.ExecuteReader();
                while (dr.Read())
                {
                    string cam = dr.GetString(0), lab = dr.GetString(1);
                    if (configOnly && !ConfigAllows(cam, lab, dr.GetDouble(3))) continue;
                    DateTime local = ToLocal(dr.GetDouble(2));
                    st.total++;
                    cameras.Add(cam);
                    labels[lab] = labels.GetValueOrDefault(lab) + 1;
                    if (!st.matrix.TryGetValue(cam, out var row)) st.matrix[cam] = row = new Dictionary<string, int>();
                    row[lab] = row.GetValueOrDefault(lab) + 1;
                    st.hours[local.Hour]++;
                    string day = local.ToString("yyyy-MM-dd");
                    days[day] = days.GetValueOrDefault(day) + 1;
                }
            }

            // Reviews are counted per camera only: a review holds several objects, so a label filter doesn't apply.
            // In config mode only configured cameras count.
            using (var cmd = new SqliteCommand("SELECT camera, severity, COUNT(*) FROM reviewsegment" + where + " GROUP BY camera, severity", db))
            {
                cmd.Parameters.AddWithValue("$from", st.from);
                if (camera != null) cmd.Parameters.AddWithValue("$camera", camera);
                using var dr = cmd.ExecuteReader();
                while (dr.Read())
                {
                    if (configOnly && !ConfigAllows(dr.GetString(0))) continue;
                    if (dr.GetString(1) == "alert") st.alerts += dr.GetInt32(2);
                    else if (dr.GetString(1) == "detection") st.detections += dr.GetInt32(2);
                }
            }

            if (!configOnly)
            {
                using var cmd = new SqliteCommand("SELECT camera, label, MAX(start_time) FROM event WHERE " + NotFalsePositive +
                                                  (camera != null ? " AND camera = $camera" : "") +
                                                  (label != null ? " AND label = $label" : "") + " GROUP BY camera, label", db);
                if (camera != null) cmd.Parameters.AddWithValue("$camera", camera);
                if (label != null) cmd.Parameters.AddWithValue("$label", label);
                using var dr = cmd.ExecuteReader();
                while (dr.Read())
                {
                    string cam = dr.GetString(0);
                    if (camera == null && !cameras.Contains(cam)) continue;
                    st.lastByCamera[cam] = Math.Max(st.lastByCamera.GetValueOrDefault(cam), dr.GetDouble(2));
                }
            }
            else if (cameras.Count > 0)
            {
                // The last event that passes the camera's thresholds: newest first, until every shown camera has one
                // (each of them has such an event within the period, so the scan stays within it).
                using var cmd = new SqliteCommand("SELECT camera, label, start_time, " + ScoreExpr + " AS ev_score FROM event WHERE " + NotFalsePositive +
                                                  (camera != null ? " AND camera = $camera" : "") + " ORDER BY start_time DESC", db);
                if (camera != null) cmd.Parameters.AddWithValue("$camera", camera);
                using var dr = cmd.ExecuteReader();
                while (dr.Read() && st.lastByCamera.Count < cameras.Count)
                {
                    string cam = dr.GetString(0);
                    if (!cameras.Contains(cam) || st.lastByCamera.ContainsKey(cam)) continue;
                    if (ConfigAllows(cam, dr.GetString(1), dr.GetDouble(3)))
                        st.lastByCamera[cam] = dr.GetDouble(2);
                }
            }

            st.cameras = st.matrix.OrderByDescending(kv => kv.Value.Values.Sum()).Select(kv => kv.Key).ToList();
            st.labels = labels.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();
            st.labelTotals = labels;
            st.days = days.Select(kv => new DayCount { day = kv.Key, count = kv.Value }).ToList();
            st.peakHour = st.total == 0 ? -1 : Array.IndexOf(st.hours, st.hours.Max());
            return st;
        }

        static double Unix(DateTime utc) => (utc - DateTime.UnixEpoch).TotalSeconds;
    }
}
