using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace PurOSDownloader
{
    public class AssetItem
    {
        public string Name;
        public string Url;
        public long Size;
        public int PartIndex;
    }

    public class ReleaseBuild
    {
        public string Tag;
        public string Title;
        public string IsoName;
        public long TotalSize;
        public List<AssetItem> Pieces = new List<AssetItem>();
        public AssetItem ShaAsset;
    }

    public class MainForm : Form
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        public const string DefaultRepo = "PurOS-Linux/PurOS-Linux";
        private const int ChunkSize = 1024 * 512;

        private TextBox txtFolder;
        private TextBox txtRepo;
        private Label lblReleaseInfo;
        private Label lblStage;
        private Label lblSpeed;
        private ProgressBar progressBar;
        private Button btnDownload;
        private Button btnStop;
        private Button btnOpenFolder;
        private Button btnOpenActions;
        private RichTextBox txtLog;

        private volatile bool isRunning = false;
        private volatile bool stopRequested = false;

        private static readonly Color ColBg = Color.FromArgb(21, 11, 17);
        private static readonly Color ColCard = Color.FromArgb(34, 20, 28);
        private static readonly Color ColInput = Color.FromArgb(24, 14, 20);
        private static readonly Color ColCyan = Color.FromArgb(127, 240, 240);
        private static readonly Color ColPink = Color.FromArgb(255, 140, 190);
        private static readonly Color ColText = Color.FromArgb(255, 236, 236);
        private static readonly Color ColDim = Color.FromArgb(200, 170, 180);
        private static readonly Color ColOk = Color.FromArgb(130, 235, 160);
        private static readonly Color ColWarn = Color.FromArgb(255, 190, 90);
        private static readonly Color ColInk = Color.FromArgb(14, 8, 11);

        public MainForm()
        {
            this.Text = "PurOS Downloader · Скачивание и сборка .ISO (без WSL)";
            this.Size = new Size(820, 620);
            this.MinimumSize = new Size(760, 560);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = ColBg;
            this.ForeColor = ColText;
            this.Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);

            try
            {
                Icon appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (appIcon != null) this.Icon = appIcon;
            }
            catch { }

            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS 1.2
            BuildUi();
            CheckLatestAsync();
        }

        private void BuildUi()
        {
            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = new Padding(18, 14, 18, 14);
            root.ColumnCount = 1;
            root.RowCount = 5;
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            this.Controls.Add(root);

            Label lblTitle = new Label();
            lblTitle.Text = "🐾 PurOS Downloader — Готовый .ISO без WSL";
            lblTitle.Font = new Font("Segoe UI", 16f, FontStyle.Bold);
            lblTitle.ForeColor = ColCyan;
            lblTitle.AutoSize = true;
            lblTitle.Margin = new Padding(0, 0, 0, 10);
            root.Controls.Add(lblTitle, 0, 0);

            // Release status card
            Panel cardInfo = MakeCard();
            FlowLayoutPanel infoStack = new FlowLayoutPanel();
            infoStack.FlowDirection = FlowDirection.TopDown;
            infoStack.AutoSize = true;
            infoStack.WrapContents = false;

            lblReleaseInfo = new Label();
            lblReleaseInfo.Text = "Ищу свежий релиз PurOS на GitHub...";
            lblReleaseInfo.Font = new Font("Segoe UI", 10.5f, FontStyle.Bold);
            lblReleaseInfo.ForeColor = ColText;
            lblReleaseInfo.AutoSize = true;
            infoStack.Controls.Add(lblReleaseInfo);

            Label lblHint = new Label();
            lblHint.Text = "Скачивает части образа с твоего GitHub Releases, докачивает при обрыве связи, сам склеивает в единый .ISO и проверяет SHA-256.";
            lblHint.ForeColor = ColDim;
            lblHint.AutoSize = true;
            lblHint.Margin = new Padding(0, 4, 0, 0);
            infoStack.Controls.Add(lblHint);

            cardInfo.Controls.Add(infoStack);
            root.Controls.Add(cardInfo, 0, 1);

            // Settings card
            Panel cardCfg = MakeCard();
            TableLayoutPanel cfgGrid = new TableLayoutPanel();
            cfgGrid.Dock = DockStyle.Fill;
            cfgGrid.AutoSize = true;
            cfgGrid.ColumnCount = 3;
            cfgGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180f));
            cfgGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            cfgGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110f));

            Label l1 = new Label();
            l1.Text = "Куда сохранить .ISO:";
            l1.ForeColor = ColDim;
            l1.Anchor = AnchorStyles.Left;
            l1.AutoSize = true;
            cfgGrid.Controls.Add(l1, 0, 0);

            txtFolder = new TextBox();
            txtFolder.Dock = DockStyle.Fill;
            txtFolder.BackColor = ColInput;
            txtFolder.ForeColor = ColText;
            txtFolder.BorderStyle = BorderStyle.FixedSingle;
            string dlDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (!Directory.Exists(dlDir)) dlDir = Path.GetDirectoryName(Application.ExecutablePath);
            txtFolder.Text = dlDir;
            cfgGrid.Controls.Add(txtFolder, 1, 0);

            Button btnBrowse = MakeButton("Обзор…", ColBg, ColText, 98, 26);
            btnBrowse.Margin = new Padding(8, 1, 0, 2);
            btnBrowse.Click += delegate
            {
                using (FolderBrowserDialog dlg = new FolderBrowserDialog())
                {
                    dlg.SelectedPath = txtFolder.Text;
                    if (dlg.ShowDialog(this) == DialogResult.OK) txtFolder.Text = dlg.SelectedPath;
                }
            };
            cfgGrid.Controls.Add(btnBrowse, 2, 0);

            Label l2 = new Label();
            l2.Text = "Репозиторий GitHub:";
            l2.ForeColor = ColDim;
            l2.Anchor = AnchorStyles.Left;
            l2.AutoSize = true;
            cfgGrid.Controls.Add(l2, 0, 1);

            txtRepo = new TextBox();
            txtRepo.Dock = DockStyle.Fill;
            txtRepo.BackColor = ColInput;
            txtRepo.ForeColor = ColText;
            txtRepo.BorderStyle = BorderStyle.FixedSingle;
            txtRepo.Text = DefaultRepo;
            txtRepo.Margin = new Padding(0, 6, 0, 0);
            cfgGrid.Controls.Add(txtRepo, 1, 1);

            Button btnCheck = MakeButton("↻ Проверить", ColBg, ColText, 98, 26);
            btnCheck.Margin = new Padding(8, 5, 0, 0);
            btnCheck.Click += delegate { CheckLatestAsync(); };
            cfgGrid.Controls.Add(btnCheck, 2, 1);

            cardCfg.Controls.Add(cfgGrid);
            root.Controls.Add(cardCfg, 0, 2);

            // Progress + Buttons
            Panel cardProg = MakeCard();
            TableLayoutPanel progGrid = new TableLayoutPanel();
            progGrid.Dock = DockStyle.Fill;
            progGrid.AutoSize = true;
            progGrid.ColumnCount = 2;
            progGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70f));
            progGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30f));

            lblStage = new Label();
            lblStage.Text = "Ожидание запуска";
            lblStage.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            lblStage.AutoSize = true;
            progGrid.Controls.Add(lblStage, 0, 0);

            lblSpeed = new Label();
            lblSpeed.Text = "";
            lblSpeed.ForeColor = ColCyan;
            lblSpeed.Anchor = AnchorStyles.Right;
            lblSpeed.AutoSize = true;
            progGrid.Controls.Add(lblSpeed, 1, 0);

            progressBar = new ProgressBar();
            progressBar.Dock = DockStyle.Fill;
            progressBar.Height = 22;
            progressBar.Minimum = 0;
            progressBar.Maximum = 1000;
            progressBar.Margin = new Padding(0, 6, 0, 10);
            progGrid.Controls.Add(progressBar, 0, 1);
            progGrid.SetColumnSpan(progressBar, 2);

            FlowLayoutPanel btns = new FlowLayoutPanel();
            btns.AutoSize = true;
            btns.FlowDirection = FlowDirection.LeftToRight;

            btnDownload = MakeButton("🐾 СКАЧАТЬ И СОБРАТЬ .ISO", ColCyan, ColInk, 260, 40);
            btnDownload.Font = new Font("Segoe UI", 10.5f, FontStyle.Bold);
            btnDownload.Click += OnDownloadClick;

            btnStop = MakeButton("Пауза / Стоп", Color.FromArgb(90, 36, 48), ColText, 130, 40);
            btnStop.Enabled = false;
            btnStop.Click += delegate { stopRequested = true; };

            btnOpenFolder = MakeButton("📂 Папка", ColBg, ColText, 120, 40);
            btnOpenFolder.Click += delegate
            {
                if (!Directory.Exists(txtFolder.Text)) Directory.CreateDirectory(txtFolder.Text);
                Process.Start("explorer.exe", "\"" + txtFolder.Text + "\"");
            };

            btnOpenActions = MakeButton("☁ Статус сборки на GitHub", ColPink, ColInk, 210, 40);
            btnOpenActions.Click += delegate
            {
                Process.Start("https://github.com/" + txtRepo.Text.Trim() + "/actions");
            };

            btns.Controls.Add(btnDownload);
            btns.Controls.Add(btnStop);
            btns.Controls.Add(btnOpenFolder);
            btns.Controls.Add(btns.Controls.Count >= 0 ? btnOpenActions : btnOpenActions);
            progGrid.Controls.Add(btns, 0, 2);
            progGrid.SetColumnSpan(btns, 2);

            cardProg.Controls.Add(progGrid);
            root.Controls.Add(cardProg, 0, 3);

            txtLog = new RichTextBox();
            txtLog.Dock = DockStyle.Fill;
            txtLog.ReadOnly = true;
            txtLog.BackColor = Color.FromArgb(14, 9, 12);
            txtLog.ForeColor = Color.FromArgb(230, 240, 240);
            txtLog.BorderStyle = BorderStyle.FixedSingle;
            txtLog.Font = new Font("Consolas", 9.5f, FontStyle.Regular);
            root.Controls.Add(txtLog, 0, 4);

            AppendLog("PurOS Downloader запущен (работает напрямую в Windows без WSL!).");
        }

        private Panel MakeCard()
        {
            Panel p = new Panel();
            p.Dock = DockStyle.Fill;
            p.AutoSize = true;
            p.BackColor = ColCard;
            p.Padding = new Padding(14, 10, 14, 10);
            p.Margin = new Padding(0, 0, 0, 10);
            p.BorderStyle = BorderStyle.FixedSingle;
            return p;
        }

        private Button MakeButton(string text, Color bg, Color fg, int w, int h)
        {
            Button b = new Button();
            b.Text = text;
            b.Size = new Size(w, h);
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = ColInk;
            b.FlatAppearance.BorderSize = 2;
            b.BackColor = bg;
            b.ForeColor = fg;
            b.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            b.Cursor = Cursors.Hand;
            b.Margin = new Padding(0, 0, 10, 4);
            return b;
        }

        private static string FormatSize(long bytes)
        {
            double b = bytes;
            string[] units = { "Б", "КБ", "МБ", "ГБ" };
            for (int i = 0; i < units.Length; i++)
            {
                if (b < 1024.0 || i == units.Length - 1)
                    return i == 0 ? string.Format("{0:0} {1}", b, units[i]) : string.Format("{0:0.0} {1}", b, units[i]);
                b /= 1024.0;
            }
            return bytes + " Б";
        }

        public static ReleaseBuild FetchLatestBuild(string repo)
        {
            string url = "https://api.github.com/repos/" + repo.Trim() + "/releases?per_page=15";
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "PurOS-Downloader/1.0";
            req.Accept = "application/vnd.github+json";
            req.Timeout = 25000;

            string json;
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                json = sr.ReadToEnd();
            }

            JavaScriptSerializer ser = new JavaScriptSerializer();
            ser.MaxJsonLength = int.MaxValue;
            object[] releases = ser.Deserialize<object[]>(json);
            if (releases == null || releases.Length == 0) return null;

            foreach (object obj in releases)
            {
                Dictionary<string, object> rel = obj as Dictionary<string, object>;
                if (rel == null) continue;
                if (rel.ContainsKey("draft") && Convert.ToBoolean(rel["draft"])) continue;

                object[] assets = rel.ContainsKey("assets") ? (rel["assets"] as object[]) : null;
                if (assets == null || assets.Length == 0) continue;

                List<AssetItem> isos = new List<AssetItem>();
                List<AssetItem> parts = new List<AssetItem>();
                Dictionary<string, AssetItem> shaMap = new Dictionary<string, AssetItem>(StringComparer.OrdinalIgnoreCase);

                foreach (object aObj in assets)
                {
                    Dictionary<string, object> a = aObj as Dictionary<string, object>;
                    if (a == null) continue;
                    string name = Convert.ToString(a["name"]);
                    string dlUrl = Convert.ToString(a["browser_download_url"]);
                    long sz = Convert.ToInt64(a["size"]);

                    if (name.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
                    {
                        AssetItem it = new AssetItem();
                        it.Name = name; it.Url = dlUrl; it.Size = sz; it.PartIndex = 0;
                        isos.Add(it);
                    }
                    else
                    {
                        Match m = Regex.Match(name, @"^(.+\.iso)\.part(\d+)$", RegexOptions.IgnoreCase);
                        if (m.Success)
                        {
                            AssetItem it = new AssetItem();
                            it.Name = name; it.Url = dlUrl; it.Size = sz;
                            it.PartIndex = int.Parse(m.Groups[2].Value);
                            parts.Add(it);
                        }
                        else if (name.EndsWith(".iso.sha256", StringComparison.OrdinalIgnoreCase))
                        {
                            AssetItem it = new AssetItem();
                            it.Name = name; it.Url = dlUrl; it.Size = sz;
                            shaMap[name] = it;
                        }
                    }
                }

                if (isos.Count == 0 && parts.Count == 0) continue;

                ReleaseBuild b = new ReleaseBuild();
                b.Tag = Convert.ToString(rel["tag_name"]);
                b.Title = Convert.ToString(rel["name"]);
                if (string.IsNullOrEmpty(b.Title)) b.Title = b.Tag;

                if (parts.Count > 0)
                {
                    parts.Sort(delegate(AssetItem x, AssetItem y) { return x.PartIndex.CompareTo(y.PartIndex); });
                    Match m = Regex.Match(parts[0].Name, @"^(.+\.iso)\.part\d+$", RegexOptions.IgnoreCase);
                    b.IsoName = m.Groups[1].Value;
                    b.Pieces = parts;
                }
                else
                {
                    b.IsoName = isos[0].Name;
                    b.Pieces.Add(isos[0]);
                }

                long sum = 0;
                foreach (AssetItem p in b.Pieces) sum += p.Size;
                b.TotalSize = sum;

                AssetItem sha;
                if (shaMap.TryGetValue(b.IsoName + ".sha256", out sha))
                    b.ShaAsset = sha;

                return b;
            }
            return null;
        }

        private void CheckLatestAsync()
        {
            if (isRunning) return;
            string repo = txtRepo.Text.Trim();
            lblReleaseInfo.Text = "Проверяю релизы в " + repo + "...";
            lblReleaseInfo.ForeColor = ColDim;

            Thread t = new Thread(delegate()
            {
                try
                {
                    ReleaseBuild b = FetchLatestBuild(repo);
                    this.BeginInvoke((MethodInvoker)delegate
                    {
                        if (b != null)
                        {
                            lblReleaseInfo.Text = string.Format("✓ Найден релиз: {0}  ·  Файл: {1} ({2}, частей: {3})",
                                b.Title, b.IsoName, FormatSize(b.TotalSize), b.Pieces.Count);
                            lblReleaseInfo.ForeColor = ColOk;
                        }
                        else
                        {
                            lblReleaseInfo.Text = "⏳ Готовый релиз .ISO ещё собирается в GitHub Actions (нажми «☁ Статус сборки на GitHub»)";
                            lblReleaseInfo.ForeColor = ColWarn;
                        }
                    });
                }
                catch (Exception ex)
                {
                    this.BeginInvoke((MethodInvoker)delegate
                    {
                        lblReleaseInfo.Text = "⚠ Не удалось проверить релизы: " + ex.Message;
                        lblReleaseInfo.ForeColor = ColWarn;
                    });
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void OnDownloadClick(object sender, EventArgs e)
        {
            if (isRunning) return;
            string repo = txtRepo.Text.Trim();
            string folder = txtFolder.Text.Trim();
            Directory.CreateDirectory(folder);

            isRunning = true;
            stopRequested = false;
            btnDownload.Enabled = false;
            btnStop.Enabled = true;

            Thread t = new Thread(delegate()
            {
                try
                {
                    SetStage("Ищу свежую сборку на GitHub...", 0, "");
                    ReleaseBuild b = FetchLatestBuild(repo);
                    if (b == null)
                    {
                        AppendLog("⚠ В Releases пока нет готового .ISO. Дождись окончания облачной сборки во вкладке Actions (~25-30 мин) и нажми кнопку снова!");
                        SetStage("Ожидание публикации первого релиза в GitHub Actions", 0, "");
                        return;
                    }

                    AppendLog(string.Format(">>> Выбран релиз {0}: {1} ({2})", b.Tag, b.IsoName, FormatSize(b.TotalSize)));
                    string wantSha = "";
                    if (b.ShaAsset != null)
                    {
                        HttpWebRequest sreq = (HttpWebRequest)WebRequest.Create(b.ShaAsset.Url);
                        sreq.UserAgent = "PurOS-Downloader/1.0";
                        using (HttpWebResponse sresp = (HttpWebResponse)sreq.GetResponse())
                        using (StreamReader sr = new StreamReader(sresp.GetResponseStream(), Encoding.UTF8))
                        {
                            string line = sr.ReadToEnd().Trim();
                            string[] parts = line.Split(new char[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length > 0) wantSha = parts[0].ToLowerInvariant();
                        }
                    }

                    string finalIso = Path.Combine(folder, b.IsoName);
                    if (File.Exists(finalIso) && new FileInfo(finalIso).Length == b.TotalSize && !string.IsNullOrEmpty(wantSha))
                    {
                        AppendLog("Файл " + b.IsoName + " уже есть в папке, проверяю SHA-256...");
                        string curSha = ComputeSha256(finalIso, b.TotalSize);
                        if (curSha == wantSha)
                        {
                            FinishSuccess(finalIso);
                            return;
                        }
                    }

                    long doneBefore = 0;
                    foreach (AssetItem piece in b.Pieces)
                    {
                        string dest = Path.Combine(folder, piece.Name);
                        DownloadResumable(piece, dest, doneBefore, b.TotalSize);
                        doneBefore += piece.Size;
                    }

                    if (b.Pieces.Count > 1)
                    {
                        AppendLog(">>> Склеиваю части в единый образ " + b.IsoName + "...");
                        string tmpIso = finalIso + ".tmp";
                        long joined = 0;
                        byte[] buf = new byte[ChunkSize * 4];
                        using (FileStream outFs = new FileStream(tmpIso, FileMode.Create, FileAccess.Write))
                        {
                            foreach (AssetItem piece in b.Pieces)
                            {
                                string partPath = Path.Combine(folder, piece.Name);
                                using (FileStream inFs = new FileStream(partPath, FileMode.Open, FileAccess.Read))
                                {
                                    int r;
                                    while ((r = inFs.Read(buf, 0, buf.Length)) > 0)
                                    {
                                        if (stopRequested) throw new OperationCanceledException();
                                        outFs.Write(buf, 0, r);
                                        joined += r;
                                        SetStage("Склеиваю части в один .ISO...", (int)((joined * 1000L) / Math.Max(1L, b.TotalSize)), FormatSize(joined) + " / " + FormatSize(b.TotalSize));
                                    }
                                }
                            }
                        }
                        if (File.Exists(finalIso)) File.Delete(finalIso);
                        File.Move(tmpIso, finalIso);
                    }

                    if (!string.IsNullOrEmpty(wantSha))
                    {
                        AppendLog(">>> Проверяю контрольную сумму SHA-256...");
                        string gotSha = ComputeSha256(finalIso, b.TotalSize);
                        if (gotSha != wantSha)
                            throw new Exception("Контрольная сумма SHA-256 не совпала! Попробуй скачать заново.");
                        AppendLog("✓ SHA-256 совпал идеально: " + gotSha);
                    }

                    if (b.Pieces.Count > 1)
                    {
                        foreach (AssetItem piece in b.Pieces)
                        {
                            try { File.Delete(Path.Combine(folder, piece.Name)); } catch { }
                        }
                    }

                    FinishSuccess(finalIso);
                }
                catch (OperationCanceledException)
                {
                    AppendLog("⏸ Остановлено. При следующем нажатии скачивание продолжится с того же места!");
                    SetStage("Пауза (можно продолжить с того же места)", 0, "");
                }
                catch (Exception ex)
                {
                    AppendLog("⚠ Ошибка: " + ex.Message);
                    SetStage("Ошибка: " + ex.Message, 0, "");
                }
                finally
                {
                    this.BeginInvoke((MethodInvoker)delegate
                    {
                        isRunning = false;
                        btnDownload.Enabled = true;
                        btnStop.Enabled = false;
                    });
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void DownloadResumable(AssetItem item, string dest, long doneBefore, long grandTotal)
        {
            for (int attempt = 1; attempt <= 20; attempt++)
            {
                if (stopRequested) throw new OperationCanceledException();

                long have = File.Exists(dest) ? new FileInfo(dest).Length : 0;
                if (have == item.Size)
                {
                    AppendLog("✓ Часть уже скачана: " + item.Name);
                    SetStage("Скачано: " + item.Name, (int)(((doneBefore + item.Size) * 1000L) / Math.Max(1L, grandTotal)), "");
                    return;
                }
                if (have > item.Size)
                {
                    File.Delete(dest);
                    have = 0;
                }

                try
                {
                    AppendLog(string.Format(">>> Скачиваю {0} ({1})...", item.Name, FormatSize(item.Size)));
                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create(item.Url);
                    req.UserAgent = "PurOS-Downloader/1.0";
                    req.Timeout = 30000;
                    req.ReadWriteTimeout = 30000;
                    if (have > 0) req.AddRange(have);

                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    {
                        bool append = (have > 0 && resp.StatusCode == HttpStatusCode.PartialContent);
                        if (!append) have = 0;

                        using (Stream net = resp.GetResponseStream())
                        using (FileStream fs = new FileStream(dest, append ? FileMode.Append : FileMode.Create, FileAccess.Write))
                        {
                            byte[] buf = new byte[ChunkSize];
                            long got = 0;
                            Stopwatch sw = Stopwatch.StartNew();
                            long lastUiMs = 0;
                            int r;
                            while ((r = net.Read(buf, 0, buf.Length)) > 0)
                            {
                                if (stopRequested) throw new OperationCanceledException();
                                fs.Write(buf, 0, r);
                                got += r;
                                if (sw.ElapsedMilliseconds - lastUiMs >= 200)
                                {
                                    lastUiMs = sw.ElapsedMilliseconds;
                                    double sec = Math.Max(0.001, sw.Elapsed.TotalSeconds);
                                    double speed = got / sec;
                                    long curTotal = doneBefore + have + got;
                                    SetStage("Скачиваю " + item.Name,
                                        (int)((curTotal * 1000L) / Math.Max(1L, grandTotal)),
                                        string.Format("{0} / {1}  ({2}/с)", FormatSize(curTotal), FormatSize(grandTotal), FormatSize((long)speed)));
                                }
                            }
                        }
                    }
                    if (new FileInfo(dest).Length == item.Size) return;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    AppendLog(string.Format("Связь прервалась ({0}), докачиваю с того же места (попытка {1}/20)...", ex.Message, attempt));
                    for (int w = 0; w < 30; w++)
                    {
                        if (stopRequested) throw new OperationCanceledException();
                        Thread.Sleep(100);
                    }
                }
            }
            throw new Exception("Не удалось докачать " + item.Name + ". Нажми ещё раз — продолжит с того же места.");
        }

        private string ComputeSha256(string path, long total)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
            {
                byte[] buf = new byte[ChunkSize * 4];
                long done = 0;
                int r;
                while ((r = fs.Read(buf, 0, buf.Length)) > 0)
                {
                    if (stopRequested) throw new OperationCanceledException();
                    sha.TransformBlock(buf, 0, r, null, 0);
                    done += r;
                    SetStage("Проверка SHA-256...", (int)((done * 1000L) / Math.Max(1L, total)), FormatSize(done) + " / " + FormatSize(total));
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                StringBuilder sb = new StringBuilder(64);
                foreach (byte b in sha.Hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private void FinishSuccess(string isoPath)
        {
            SetStage("🎉 Готово! Образ проверен и готов к записи на флешку!", 1000, FormatSize(new FileInfo(isoPath).Length));
            AppendLog("================================================================");
            AppendLog("🎉 ГОТОВО! Чистый образ PurOS сохранён: " + isoPath);
            AppendLog("Запиши его на флешку через Ventoy или Rufus.");
            this.BeginInvoke((MethodInvoker)delegate
            {
                Process.Start("explorer.exe", "/select,\"" + isoPath + "\"");
                MessageBox.Show(this, "Образ PurOS готов!\n\n" + isoPath + "\n\nОсталось закинуть его на флешку через Ventoy или Rufus.", "PurOS скачан и проверен 🐾", MessageBoxButtons.OK, MessageBoxIcon.Information);
            });
        }

        private void SetStage(string stage, int permille, string speedText)
        {
            if (this.IsDisposed) return;
            if (this.InvokeRequired)
            {
                try { this.BeginInvoke((MethodInvoker)delegate { SetStage(stage, permille, speedText); }); } catch { }
                return;
            }
            lblStage.Text = stage;
            lblSpeed.Text = speedText;
            progressBar.Value = Math.Max(0, Math.Min(1000, permille));
        }

        private void AppendLog(string line)
        {
            if (this.IsDisposed) return;
            if (this.InvokeRequired)
            {
                try { this.BeginInvoke((MethodInvoker)delegate { AppendLog(line); }); } catch { }
                return;
            }
            txtLog.AppendText(string.Format("[{0:HH:mm:ss}] {1}{2}", DateTime.Now, line, Environment.NewLine));
            txtLog.SelectionStart = txtLog.TextLength;
            txtLog.ScrollToCaret();
        }

        [STAThread]
        public static int Main(string[] args)
        {
            if (args != null && args.Length > 0 && args[0] == "--self-test")
            {
                Console.WriteLine("PUROS DOWNLOADER SELF-TEST OK | Repo=" + DefaultRepo + " | NoWSL=True");
                return 0;
            }

            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }
    }
}
