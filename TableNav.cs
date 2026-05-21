using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Microsoft.Web.WebView2.WinForms;

class Program {
    [DllImport("user32.dll")]
    static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [STAThread]
    static void Main(string[] args) {
        // PerMonitorV2 DPI awareness — must be set before any UI is created.
        // Without this, Windows scales a 96-DPI bitmap up to the display DPI → blurry WebView2.
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch {}
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        string csv = args.Length > 0 && File.Exists(args[0]) ? args[0] : null;

        // Single-instance: forward to a running instance if one exists.
        string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (TryForwardToExistingInstance(exeDir, csv)) return;

        Application.Run(new TabXApp(csv));
    }

    // Returns true if an existing instance was found and the request was forwarded.
    static bool TryForwardToExistingInstance(string exeDir, string csvPath) {
        try {
            string portFile = Path.Combine(exeDir, "TableNav.port");
            if (!File.Exists(portFile)) return false;
            string txt = File.ReadAllText(portFile, Encoding.UTF8).Trim();
            int port;
            if (!int.TryParse(txt, out port)) return false;

            // Verify the instance is still alive.
            try {
                var hb = (HttpWebRequest)WebRequest.Create("http://localhost:" + port + "/api/heartbeat");
                hb.Method = "POST";
                hb.ContentLength = 0;
                hb.GetResponse().Close();
            } catch { return false; }

            if (csvPath != null) {
                var req = (HttpWebRequest)WebRequest.Create(
                    "http://localhost:" + port + "/api/open?path=" + Uri.EscapeDataString(csvPath));
                req.Method = "POST";
                req.ContentLength = 0;
                req.GetResponse().Close();
            } else {
                var req = (HttpWebRequest)WebRequest.Create(
                    "http://localhost:" + port + "/api/activate");
                req.Method = "POST";
                req.ContentLength = 0;
                req.GetResponse().Close();
            }
            return true;
        } catch { return false; }
    }
}

class TabXApp : ApplicationContext {
    internal readonly string exeDir;
    volatile string currentPath;
    volatile string[] pendingSessionPaths; // extra paths to serve via /api/session on startup
    int port;
    HttpListener http;
    NotifyIcon tray;
    TabXForm mainForm;
    bool quitting;

    public TabXApp(string csvPath) {
        exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

        // Session restore takes priority over the command-line arg and lastpath.
        string[] session = ReadSession();
        if (session != null && session.Length > 0) {
            currentPath = session[0];
            pendingSessionPaths = session.Length > 1 ? session.Skip(1).ToArray() : null;
        } else {
            currentPath = csvPath;
            if (currentPath == null) {
                string last = ReadLastPath();
                if (last != null && File.Exists(last)) currentPath = last;
            }
        }
        port = GetFreePort();

        SetupTray();

        try {
            http = new HttpListener();
            http.Prefixes.Add("http://localhost:" + port + "/");
            http.Start();
        } catch (Exception ex) {
            MessageBox.Show("TableNav could not start the local server:\n\n" + ex.Message +
                "\n\nTry running as administrator.", "TableNav", MessageBoxButtons.OK, MessageBoxIcon.Error);
            ExitThread();
            return;
        }

        // Write port so a second instance can find us.
        try { File.WriteAllText(Path.Combine(exeDir, "TableNav.port"), port.ToString(), new UTF8Encoding(false)); } catch {}

        var t = new Thread(ServerLoop);
        t.IsBackground = true;
        t.Start();

        mainForm = new TabXForm("http://localhost:" + port + "/");
        mainForm.Icon = MakeIcon();

        // Cancel the close synchronously; let JS decide whether to actually quit
        // (JS calls /api/shutdown when the user confirms, which then calls Quit()).
        mainForm.FormClosing += (s, e) => {
            if (quitting) return;
            e.Cancel = true;
            mainForm.ExecuteScript("requestAppQuit()");
        };

        mainForm.Show();
    }

    void SetupTray() {
        tray = new NotifyIcon();
        tray.Text = "TableNav";
        tray.Icon = MakeIcon();
        tray.Visible = true;

        var menu = new ContextMenu();
        menu.MenuItems.Add("Show Window", (s, e) => ShowWindow());
        menu.MenuItems.Add("-");
        menu.MenuItems.Add("Exit TableNav", (s, e) => Quit());
        tray.ContextMenu = menu;
        tray.DoubleClick += (s, e) => ShowWindow();
    }

    void ShowWindow() {
        if (mainForm != null && !mainForm.IsDisposed) {
            if (mainForm.WindowState == FormWindowState.Minimized)
                mainForm.WindowState = FormWindowState.Normal;
            mainForm.BringToFront();
            mainForm.Activate();
        }
    }

    void Quit() {
        if (quitting) return;
        quitting = true;
        tray.Visible = false;
        try { File.Delete(Path.Combine(exeDir, "TableNav.port")); } catch {}
        try { http.Stop(); } catch {}
        try { if (mainForm != null && !mainForm.IsDisposed) mainForm.Close(); } catch {}
        ExitThread();
    }

    // ── HTTP server ──────────────────────────────────────────────────────────

    void ServerLoop() {
        while (http.IsListening) {
            try {
                var ctx = http.GetContext();
                ThreadPool.QueueUserWorkItem(_ => SafeHandle(ctx));
            } catch { break; }
        }
    }

    void SafeHandle(object o) {
        var ctx = (HttpListenerContext)o;
        try { Handle(ctx); } catch { try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch {} }
    }

    void Handle(HttpListenerContext ctx) {
        string path = ctx.Request.Url.LocalPath.TrimEnd('/');
        if (path == "") path = "/";
        string method = ctx.Request.HttpMethod;

        ctx.Response.Headers.Add("Cache-Control", "no-cache, no-store, must-revalidate");
        ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
        ctx.Response.Headers.Add("Access-Control-Expose-Headers", "X-File-Path");

        if (method == "OPTIONS") {
            ctx.Response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            ctx.Response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, X-Save-Path");
            ctx.Response.StatusCode = 204;
            ctx.Response.Close();
            return;
        }

        if (method == "GET" && path == "/manifest.json") {
            Send(ctx, 200, "application/manifest+json; charset=utf-8",
                "{\"name\":\"TableNav\",\"short_name\":\"TableNav\"," +
                "\"display\":\"standalone\",\"start_url\":\"/\"," +
                "\"theme_color\":\"#f3f4f6\",\"background_color\":\"#ffffff\"}");
        } else if (method == "GET" && (path == "/" || path == "/index.html")) {
            ServeHtml(ctx);
        } else if (method == "GET" && path == "/api/load") {
            ServeLoad(ctx);
        } else if (method == "POST" && path == "/api/save") {
            HandleSave(ctx);
        } else if (method == "POST" && path == "/api/saveas") {
            HandleSaveAs(ctx);
        } else if (method == "POST" && path == "/api/heartbeat") {
            Send(ctx, 200, "application/json; charset=utf-8", "{\"ok\":true}");
        } else if (method == "POST" && path == "/api/shutdown") {
            Send(ctx, 200, "application/json; charset=utf-8", "{\"ok\":true}");
            try { mainForm.Invoke((Action)Quit); } catch {}
        } else if (method == "POST" && path == "/api/open") {
            HandleOpen(ctx);
        } else if (method == "POST" && path == "/api/convert-xlsx") {
            HandleConvertXlsx(ctx);
        } else if (method == "POST" && path == "/api/activate") {
            Send(ctx, 200, "application/json; charset=utf-8", "{\"ok\":true}");
            try { mainForm.Invoke((Action)ShowWindow); } catch {}
        } else if (method == "GET" && path == "/api/session") {
            HandleSession(ctx);
        } else if (method == "POST" && path == "/api/restart") {
            HandleRestart(ctx);
        } else {
            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
        }
    }

    void HandleOpen(HttpListenerContext ctx) {
        string filePath = ctx.Request.QueryString["path"];
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) {
            Send(ctx, 400, "application/json; charset=utf-8", "{\"ok\":false,\"error\":\"Invalid path\"}");
            return;
        }
        Send(ctx, 200, "application/json; charset=utf-8", "{\"ok\":true}");
        if (mainForm == null || mainForm.IsDisposed) return;
        string escaped = JsonEsc(filePath);
        try {
            mainForm.Invoke((Action)(() => {
                ShowWindow();
                mainForm.WindowState = FormWindowState.Maximized;
                mainForm.ExecuteScript("openTabFromServer(\"" + escaped + "\")");
            }));
        } catch {}
    }

    void ServeHtml(HttpListenerContext ctx) {
        string htmlPath = Path.Combine(exeDir, "TableNav.html");
        if (!File.Exists(htmlPath)) {
            string msg = "TableNav.html not found in: " + exeDir;
            Send(ctx, 404, "text/plain; charset=utf-8", msg);
            return;
        }
        byte[] bytes = File.ReadAllBytes(htmlPath);
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.Close();
    }

    void ServeLoad(HttpListenerContext ctx) {
        string qpath = ctx.Request.QueryString["path"];
        if (!string.IsNullOrEmpty(qpath) && File.Exists(qpath))
            currentPath = qpath;

        if (currentPath != null && currentPath.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) {
            try {
                var sheets = XlsxToSheets(currentPath);
                string json = SheetsToJson(sheets);
                byte[] jb = Encoding.UTF8.GetBytes(json);
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/x-xlsx-sheets; charset=utf-8";
                ctx.Response.Headers.Add("X-File-Path", currentPath);
                ctx.Response.ContentLength64 = jb.Length;
                ctx.Response.OutputStream.Write(jb, 0, jb.Length);
                ctx.Response.Close();
            } catch (Exception ex) {
                Send(ctx, 500, "text/plain; charset=utf-8", "Error reading xlsx: " + ex.Message);
            }
            return;
        }

        string data = (currentPath != null) ? SafeRead(currentPath) : "";
        byte[] bytes = Encoding.UTF8.GetBytes(data);
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/plain; charset=utf-8";
        if (currentPath != null) ctx.Response.Headers.Add("X-File-Path", currentPath);
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.Close();
    }

    void HandleConvertXlsx(HttpListenerContext ctx) {
        try {
            byte[] body;
            using (var ms = new MemoryStream()) {
                ctx.Request.InputStream.CopyTo(ms);
                body = ms.ToArray();
            }
            List<KeyValuePair<string,string>> sheets;
            using (var ms = new MemoryStream(body))
                sheets = XlsxToSheets(ms);
            Send(ctx, 200, "application/x-xlsx-sheets; charset=utf-8", SheetsToJson(sheets));
        } catch (Exception ex) {
            Send(ctx, 400, "application/json; charset=utf-8", "{\"error\":\"" + JsonEsc(ex.Message) + "\"}");
        }
    }

    void HandleSave(HttpListenerContext ctx) {
        string path = ctx.Request.QueryString["path"];
        if (string.IsNullOrEmpty(path)) path = ctx.Request.Headers["X-Save-Path"];
        string data = ReadBody(ctx);

        if (string.IsNullOrEmpty(path)) {
            Send(ctx, 400, "application/json; charset=utf-8", "{\"ok\":false,\"error\":\"Missing path\"}");
            return;
        }
        try {
            File.WriteAllText(path, data, new UTF8Encoding(false));
            currentPath = path;
            SaveLastPath(path);
            Send(ctx, 200, "application/json; charset=utf-8", "{\"ok\":true}");
        } catch (Exception ex) {
            Send(ctx, 500, "application/json; charset=utf-8",
                "{\"ok\":false,\"error\":\"" + JsonEsc(ex.Message) + "\"}");
        }
    }

    void HandleSaveAs(HttpListenerContext ctx) {
        string data = ReadBody(ctx);
        string suggested = ctx.Request.QueryString["name"] ?? "untitled.csv";
        string selPath = null;

        var t = new Thread(() => {
            var dlg = new SaveFileDialog();
            dlg.Filter = "CSV Files (*.csv)|*.csv|TSV Files (*.tsv)|*.tsv|Text Files (*.txt)|*.txt|All Files (*.*)|*.*";
            dlg.Title = "Save CSV File — TableNav";
            dlg.FileName = suggested;
            if (currentPath != null) {
                try { dlg.InitialDirectory = Path.GetDirectoryName(currentPath); } catch {}
            } else {
                dlg.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            }
            using (var owner = new Form {
                TopMost = true, ShowInTaskbar = false,
                Size = new Size(1, 1), StartPosition = FormStartPosition.Manual,
                Location = new Point(-32000, -32000)
            }) {
                owner.Show();
                if (dlg.ShowDialog(owner) == DialogResult.OK) selPath = dlg.FileName;
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();

        if (selPath == null) {
            Send(ctx, 200, "application/json; charset=utf-8", "{\"ok\":false,\"cancelled\":true}");
            return;
        }
        try {
            File.WriteAllText(selPath, data, new UTF8Encoding(false));
            currentPath = selPath;
            SaveLastPath(selPath);
            Send(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":true,\"path\":\"" + JsonEsc(selPath) + "\"}");
        } catch (Exception ex) {
            Send(ctx, 500, "application/json; charset=utf-8",
                "{\"ok\":false,\"error\":\"" + JsonEsc(ex.Message) + "\"}");
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    string ReadLastPath() {
        try {
            string f = Path.Combine(exeDir, "TableNav.lastpath");
            if (File.Exists(f)) return File.ReadAllText(f, Encoding.UTF8).Trim();
        } catch {}
        return null;
    }

    void SaveLastPath(string path) {
        try { File.WriteAllText(Path.Combine(exeDir, "TableNav.lastpath"), path, new UTF8Encoding(false)); } catch {}
    }

    void HandleSession(HttpListenerContext ctx) {
        string[] paths = pendingSessionPaths;
        pendingSessionPaths = null;
        if (paths == null) paths = new string[0];
        var sb = new StringBuilder("[");
        for (int i = 0; i < paths.Length; i++) {
            if (i > 0) sb.Append(',');
            sb.Append('"'); sb.Append(JsonEsc(paths[i])); sb.Append('"');
        }
        sb.Append("]");
        Send(ctx, 200, "application/json; charset=utf-8", sb.ToString());
    }

    void HandleRestart(HttpListenerContext ctx) {
        string body = ReadBody(ctx);
        // Body is a JSON array of path strings — parse with a simple regex-free approach.
        var paths = new List<string>();
        // Strip outer brackets and split on quoted strings.
        int i = 0;
        while (i < body.Length) {
            if (body[i] == '"') {
                i++;
                var sb = new StringBuilder();
                while (i < body.Length && body[i] != '"') {
                    if (body[i] == '\\' && i + 1 < body.Length) { i++; sb.Append(body[i]); }
                    else sb.Append(body[i]);
                    i++;
                }
                string p = sb.ToString();
                if (p.Length > 0 && File.Exists(p)) paths.Add(p);
            }
            i++;
        }
        WriteSession(paths.ToArray());
        // Delete port file so the new instance doesn't forward to us.
        try { File.Delete(Path.Combine(exeDir, "TableNav.port")); } catch {}
        string exePath = Assembly.GetExecutingAssembly().Location;
        try { Process.Start(exePath); } catch {}
        Send(ctx, 200, "application/json; charset=utf-8", "{\"ok\":true}");
        try { mainForm.Invoke((Action)Quit); } catch {}
    }

    void WriteSession(string[] paths) {
        try {
            File.WriteAllLines(Path.Combine(exeDir, "TableNav.session"), paths, new UTF8Encoding(false));
        } catch {}
    }

    string[] ReadSession() {
        try {
            string f = Path.Combine(exeDir, "TableNav.session");
            if (!File.Exists(f)) return null;
            var lines = File.ReadAllLines(f, Encoding.UTF8)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && File.Exists(l))
                .ToArray();
            File.Delete(f);
            return lines.Length > 0 ? lines : null;
        } catch { return null; }
    }

    static int GetFreePort() {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    static string SafeRead(string path) {
        try { return File.ReadAllText(path, Encoding.UTF8); }
        catch { try { return File.ReadAllText(path, Encoding.Default); } catch { return ""; } }
    }

    static string ReadBody(HttpListenerContext ctx) {
        using (var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
            return sr.ReadToEnd();
    }

    static void Send(HttpListenerContext ctx, int status, string ct, string text) {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = ct;
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.Close();
    }

    static string BuildJson(string path, string data) {
        var sb = new StringBuilder();
        sb.Append("{\"path\":");
        if (path == null) sb.Append("null");
        else { sb.Append('"'); sb.Append(JsonEsc(path)); sb.Append('"'); }
        sb.Append(",\"data\":\"");
        sb.Append(JsonEsc(data ?? ""));
        sb.Append("\"}");
        return sb.ToString();
    }

    static string JsonEsc(string s) {
        if (s == null) return "";
        var sb = new StringBuilder(s.Length + 16);
        foreach (char c in s) {
            switch (c) {
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n");  break;
                case '\r': sb.Append("\\r");  break;
                case '\t': sb.Append("\\t");  break;
                default:
                    if (c < 32) sb.Append("\\u" + ((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    static string ExtractField(string json, string key) {
        if (json == null) return null;
        string needle = "\"" + key + "\":\"";
        int start = json.IndexOf(needle);
        if (start < 0) return null;
        start += needle.Length;
        var sb = new StringBuilder();
        bool esc = false;
        for (int i = start; i < json.Length; i++) {
            char c = json[i];
            if (esc) {
                switch (c) {
                    case '"':  sb.Append('"');  break;
                    case '\\': sb.Append('\\'); break;
                    case 'n':  sb.Append('\n'); break;
                    case 'r':  sb.Append('\r'); break;
                    case 't':  sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 < json.Length) {
                            int code;
                            if (int.TryParse(json.Substring(i + 1, 4),
                                System.Globalization.NumberStyles.HexNumber, null, out code))
                                sb.Append((char)code);
                            i += 4;
                        }
                        break;
                    default: sb.Append(c); break;
                }
                esc = false;
            } else if (c == '\\') { esc = true; }
            else if (c == '"') break;
            else sb.Append(c);
        }
        return sb.ToString();
    }

    // ── XLSX → CSV conversion ────────────────────────────────────────────────

    static List<KeyValuePair<string,string>> XlsxToSheets(string path) {
        using (var fs = File.OpenRead(path))
            return XlsxToSheets(fs);
    }

    static List<KeyValuePair<string,string>> XlsxToSheets(Stream input) {
        using (var zip = new ZipArchive(input, ZipArchiveMode.Read)) {
            var sst = XlsxReadSST(zip);
            var entries = XlsxSheetEntries(zip);
            var result = new List<KeyValuePair<string,string>>();
            foreach (var kv in entries) {
                var entry = zip.GetEntry(kv.Value);
                if (entry != null)
                    result.Add(new KeyValuePair<string,string>(kv.Key, XlsxSheetToCSV(entry, sst)));
            }
            if (result.Count == 0) throw new Exception("No worksheets found in xlsx file.");
            return result;
        }
    }

    static List<string> XlsxReadSST(ZipArchive zip) {
        var sst = new List<string>();
        var entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry != null) {
            using (var s = entry.Open()) {
                var doc = XDocument.Load(s);
                var ns = doc.Root.GetDefaultNamespace();
                foreach (var si in doc.Root.Elements(ns + "si"))
                    sst.Add(string.Concat(si.Descendants(ns + "t").Select(t => t.Value)));
            }
        }
        return sst;
    }

    // Returns ordered list of (sheetName, zipEntryPath) from workbook.
    static List<KeyValuePair<string,string>> XlsxSheetEntries(ZipArchive zip) {
        var result = new List<KeyValuePair<string,string>>();
        try {
            var relMap = new Dictionary<string,string>();
            var relsEntry = zip.GetEntry("xl/_rels/workbook.xml.rels");
            if (relsEntry != null) {
                using (var s = relsEntry.Open()) {
                    var doc = XDocument.Load(s);
                    var pkgNs = XNamespace.Get("http://schemas.openxmlformats.org/package/2006/relationships");
                    foreach (var rel in doc.Root.Elements(pkgNs + "Relationship")) {
                        string id = (string)rel.Attribute("Id");
                        string target = (string)rel.Attribute("Target");
                        if (id != null && target != null) relMap[id] = target;
                    }
                }
            }
            var wbEntry = zip.GetEntry("xl/workbook.xml");
            if (wbEntry != null) {
                using (var s = wbEntry.Open()) {
                    var doc = XDocument.Load(s);
                    var ns = doc.Root.GetDefaultNamespace();
                    var rNs = XNamespace.Get("http://schemas.openxmlformats.org/officeDocument/2006/relationships");
                    foreach (var sheet in doc.Descendants(ns + "sheet")) {
                        string name = (string)sheet.Attribute("name") ?? "Sheet";
                        string relId = (string)sheet.Attribute(rNs + "id");
                        string target;
                        if (relId == null || !relMap.TryGetValue(relId, out target)) continue;
                        string entryPath = target.StartsWith("/") ? target.TrimStart('/') : "xl/" + target;
                        result.Add(new KeyValuePair<string,string>(name, entryPath));
                    }
                }
            }
        } catch { }
        if (result.Count == 0) {
            var e = zip.GetEntry("xl/worksheets/sheet1.xml");
            if (e != null) result.Add(new KeyValuePair<string,string>("Sheet1", "xl/worksheets/sheet1.xml"));
        }
        return result;
    }

    static string XlsxSheetToCSV(ZipArchiveEntry wsEntry, List<string> sst) {
        var data = new SortedDictionary<int, SortedDictionary<int, string>>();
        int maxRow = -1, maxCol = 0;
        using (var s = wsEntry.Open()) {
            var doc = XDocument.Load(s);
            var ns = doc.Root.GetDefaultNamespace();
            foreach (var rowEl in doc.Descendants(ns + "row")) {
                string rowAttr = (string)rowEl.Attribute("r");
                int rowIdx;
                if (rowAttr == null || !int.TryParse(rowAttr, out rowIdx)) continue;
                rowIdx--;
                if (rowIdx > maxRow) maxRow = rowIdx;
                var rowData = new SortedDictionary<int, string>();
                foreach (var cellEl in rowEl.Elements(ns + "c")) {
                    string cellRef = (string)cellEl.Attribute("r");
                    if (cellRef == null) continue;
                    int colIdx = XlsxColIndex(cellRef);
                    if (colIdx > maxCol) maxCol = colIdx;
                    string t = (string)cellEl.Attribute("t") ?? "";
                    string val;
                    if (t == "s") {
                        string v = (string)cellEl.Element(ns + "v");
                        int si;
                        val = (v != null && int.TryParse(v, out si) && si >= 0 && si < sst.Count) ? sst[si] : "";
                    } else if (t == "inlineStr") {
                        val = string.Concat(cellEl.Descendants(ns + "t").Select(x => x.Value));
                    } else if (t == "b") {
                        val = (string)cellEl.Element(ns + "v") == "1" ? "TRUE" : "FALSE";
                    } else {
                        val = (string)cellEl.Element(ns + "v") ?? "";
                    }
                    if (val != "") rowData[colIdx] = val;
                }
                if (rowData.Count > 0) data[rowIdx] = rowData;
            }
        }
        var sb = new StringBuilder();
        for (int r = 0; r <= maxRow; r++) {
            SortedDictionary<int, string> rowData;
            data.TryGetValue(r, out rowData);
            for (int c = 0; c <= maxCol; c++) {
                if (c > 0) sb.Append(',');
                string val = (rowData != null && rowData.ContainsKey(c)) ? rowData[c] : "";
                if (val.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0) {
                    sb.Append('"');
                    sb.Append(val.Replace("\"", "\"\""));
                    sb.Append('"');
                } else {
                    sb.Append(val);
                }
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    static string SheetsToJson(List<KeyValuePair<string,string>> sheets) {
        var sb = new StringBuilder("[");
        for (int i = 0; i < sheets.Count; i++) {
            if (i > 0) sb.Append(',');
            sb.Append("{\"name\":\""); sb.Append(JsonEsc(sheets[i].Key));
            sb.Append("\",\"csv\":\""); sb.Append(JsonEsc(sheets[i].Value));
            sb.Append("\"}");
        }
        sb.Append("]");
        return sb.ToString();
    }

    static int XlsxColIndex(string cellRef) {
        int col = 0;
        foreach (char ch in cellRef) {
            if (ch < 'A' || ch > 'Z') break;
            col = col * 26 + (ch - 'A' + 1);
        }
        return col - 1;
    }

    internal static Icon MakeIcon() {
        try {
            string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string icoPath = Path.Combine(exeDir, "Logo.ico");
            if (File.Exists(icoPath)) return new Icon(icoPath);
        } catch {}
        return SystemIcons.Application;
    }
}

class TabXForm : Form {
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    const int DWMWA_CAPTION_COLOR = 35;

    WebView2 webView;
    public WebView2 WebView { get { return webView; } }

    public TabXForm(string url) {
        Text = "TableNav";
        Size = new Size(1200, 800);
        MinimumSize = new Size(640, 480);
        StartPosition = FormStartPosition.CenterScreen;

        webView = new WebView2();
        webView.Dock = DockStyle.Fill;
        Controls.Add(webView);

        // Await initialization before navigating — Source set in constructor can be
        // silently dropped if the WebView2 browser process isn't ready yet.
        Load += async (s, e) => {
            try {
                await webView.EnsureCoreWebView2Async(null);
                webView.CoreWebView2.Navigate(url);
            } catch (Exception ex) {
                MessageBox.Show(
                    "WebView2 could not initialize.\n\nEnsure Microsoft Edge is installed and up to date.\n\n" + ex.Message,
                    "TableNav — WebView2 Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            WindowState = FormWindowState.Maximized;
        };
    }

    public void ExecuteScript(string script) {
        if (webView != null && webView.CoreWebView2 != null)
            webView.CoreWebView2.ExecuteScriptAsync(script);
    }

    protected override void OnHandleCreated(EventArgs e) {
        base.OnHandleCreated(e);
        try {
            // #f3f4f6 as COLORREF (0x00BBGGRR): B=0xF6, G=0xF4, R=0xF3
            int color = 0x00F6F4F3;
            DwmSetWindowAttribute(Handle, DWMWA_CAPTION_COLOR, ref color, 4);
        } catch {}
    }
}
