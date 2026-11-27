using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace PurOSBuilder
{
    public class MainForm : Form
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        private TextBox txtSource;
        private TextBox txtOutput;
        private TextBox txtVideos;
        private TextBox txtIsoName;
        private CheckBox chkFurrBoot;
        private CheckBox chkAurWalls;
        private Label lblStatusSrc;
        private Label lblStatusWsl;
        private Label lblStatusArch;
        private Button btnSetupWsl;
        private Button btnBuild;
        private Button btnStop;
        private Button btnOpenOut;
        private ProgressBar progressBar;
        private RichTextBox txtLog;

        private Process runningProc;
        private volatile bool isBuilding = false;
        private string detectedDistro = "";
        private bool wslInstalled = false;

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
            this.Text = "PurOS ISO Builder · Сборщик установочного образа PurOS";
            this.Size = new Size(940, 740);
            this.MinimumSize = new Size(860, 660);
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

            BuildUi();
            AutoDetectPaths();
            RefreshEnvironment();
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
            lblTitle.Text = "🐾 PurOS ISO Builder — Сборка своего дистрибутива";
            lblTitle.Font = new Font("Segoe UI", 16f, FontStyle.Bold);
            lblTitle.ForeColor = ColCyan;
            lblTitle.AutoSize = true;
            lblTitle.Margin = new Padding(0, 0, 0, 10);
            root.Controls.Add(lblTitle, 0, 0);

            Panel cardEnv = MakeCard();
            TableLayoutPanel envGrid = new TableLayoutPanel();
            envGrid.Dock = DockStyle.Fill;
            envGrid.AutoSize = true;
            envGrid.ColumnCount = 2;
            envGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 68f));
            envGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32f));

            FlowLayoutPanel statusStack = new FlowLayoutPanel();
            statusStack.FlowDirection = FlowDirection.TopDown;
            statusStack.AutoSize = true;
            statusStack.WrapContents = false;
            lblStatusSrc = MakeStatusLabel("Проверка исходников PurOS...");
            lblStatusWsl = MakeStatusLabel("Проверка WSL2...");
            lblStatusArch = MakeStatusLabel("Проверка сборочного контейнера Arch Linux...");
            statusStack.Controls.Add(lblStatusSrc);
            statusStack.Controls.Add(lblStatusWsl);
            statusStack.Controls.Add(lblStatusArch);
            envGrid.Controls.Add(statusStack, 0, 0);

            FlowLayoutPanel envBtns = new FlowLayoutPanel();
            envBtns.FlowDirection = FlowDirection.TopDown;
            envBtns.AutoSize = true;
            envBtns.Anchor = AnchorStyles.Right | AnchorStyles.Top;

            btnSetupWsl = MakeButton("⚙ Подготовить WSL2 + Arch", ColPink, ColInk, 240, 36);
            btnSetupWsl.Click += OnSetupWslClick;

            Button btnRefresh = MakeButton("↻ Проверить снова", ColCard, ColText, 240, 30);
            btnRefresh.Click += delegate { RefreshEnvironment(); };

            envBtns.Controls.Add(btnSetupWsl);
            envBtns.Controls.Add(btnRefresh);
            envGrid.Controls.Add(envBtns, 1, 0);

            cardEnv.Controls.Add(envGrid);
            root.Controls.Add(cardEnv, 0, 1);

            Panel cardCfg = MakeCard();
            TableLayoutPanel cfgGrid = new TableLayoutPanel();
            cfgGrid.Dock = DockStyle.Fill;
            cfgGrid.AutoSize = true;
            cfgGrid.ColumnCount = 3;
            cfgGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210f));
            cfgGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            cfgGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110f));

            txtSource = AddPathRow(cfgGrid, 0, "Папка исходников PurOS:", true);
            txtOutput = AddPathRow(cfgGrid, 1, "Куда сохранить .ISO:", false);
            txtVideos = AddPathRow(cfgGrid, 2, "Папка своих .MP4 обоев:", true);

            Label lblIso = new Label();
            lblIso.Text = "Имя образа (латиницей):";
            lblIso.ForeColor = ColDim;
            lblIso.Anchor = AnchorStyles.Left;
            lblIso.AutoSize = true;
            cfgGrid.Controls.Add(lblIso, 0, 3);

            FlowLayoutPanel optsRow = new FlowLayoutPanel();
            optsRow.AutoSize = true;
            optsRow.FlowDirection = FlowDirection.LeftToRight;
            optsRow.Margin = new Padding(0, 4, 0, 0);

            txtIsoName = new TextBox();
            txtIsoName.Text = "puros";
            txtIsoName.Width = 140;
            txtIsoName.BackColor = ColInput;
            txtIsoName.ForeColor = ColText;
            txtIsoName.BorderStyle = BorderStyle.FixedSingle;
            optsRow.Controls.Add(txtIsoName);

            chkFurrBoot = new CheckBox();
            chkFurrBoot.Text = "Графическое UEFI-меню (Rust)";
            chkFurrBoot.Checked = true;
            chkFurrBoot.AutoSize = true;
            chkFurrBoot.Margin = new Padding(18, 3, 0, 0);
            optsRow.Controls.Add(chkFurrBoot);

            chkAurWalls = new CheckBox();
            chkAurWalls.Text = "Плагины видео-обоев (AUR)";
            chkAurWalls.Checked = true;
            chkAurWalls.AutoSize = true;
            chkAurWalls.Margin = new Padding(16, 3, 0, 0);
            optsRow.Controls.Add(chkAurWalls);

            cfgGrid.Controls.Add(optsRow, 1, 3);
            cfgGrid.SetColumnSpan(optsRow, 2);

            cardCfg.Controls.Add(cfgGrid);
            root.Controls.Add(cardCfg, 0, 2);

            FlowLayoutPanel actionRow = new FlowLayoutPanel();
            actionRow.Dock = DockStyle.Fill;
            actionRow.AutoSize = true;
            actionRow.Margin = new Padding(0, 4, 0, 8);

            btnBuild = MakeButton("🐾 СОБРАТЬ PUROS (.ISO)", ColCyan, ColInk, 270, 42);
            btnBuild.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
            btnBuild.Click += OnBuildClick;

            btnStop = MakeButton("✕ Остановить", Color.FromArgb(90, 36, 48), ColText, 140, 42);
            btnStop.Enabled = false;
            btnStop.Click += OnStopClick;

            btnOpenOut = MakeButton("📂 Открыть папку с ISO", ColCard, ColText, 210, 42);
            btnOpenOut.Click += delegate
            {
                if (!Directory.Exists(txtOutput.Text)) Directory.CreateDirectory(txtOutput.Text);
                Process.Start("explorer.exe", "\"" + txtOutput.Text + "\"");
            };

            progressBar = new ProgressBar();
            progressBar.Width = 240;
            progressBar.Height = 20;
            progressBar.Margin = new Padding(14, 12, 0, 0);
            progressBar.Style = ProgressBarStyle.Blocks;

            actionRow.Controls.Add(btnBuild);
            actionRow.Controls.Add(btnStop);
            actionRow.Controls.Add(btnOpenOut);
            actionRow.Controls.Add(progressBar);
            root.Controls.Add(actionRow, 0, 3);

            txtLog = new RichTextBox();
            txtLog.Dock = DockStyle.Fill;
            txtLog.ReadOnly = true;
            txtLog.BackColor = Color.FromArgb(14, 9, 12);
            txtLog.ForeColor = Color.FromArgb(230, 240, 240);
            txtLog.BorderStyle = BorderStyle.FixedSingle;
            txtLog.Font = new Font("Consolas", 9.5f, FontStyle.Regular);
            root.Controls.Add(txtLog, 0, 4);

            AppendLog("PurOS ISO Builder готов к работе! Также в проекте настроен .github/workflows/build-iso.yml для бесплатной сборки в облаке GitHub.");
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

        private Label MakeStatusLabel(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Margin = new Padding(0, 2, 0, 3);
            l.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            return l;
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
            b.Margin = new Padding(0, 0, 10, 6);
            return b;
        }

        private TextBox AddPathRow(TableLayoutPanel grid, int row, string title, bool checkExists)
        {
            Label lbl = new Label();
            lbl.Text = title;
            lbl.ForeColor = ColDim;
            lbl.Anchor = AnchorStyles.Left;
            lbl.AutoSize = true;
            grid.Controls.Add(lbl, 0, row);

            TextBox tb = new TextBox();
            tb.Dock = DockStyle.Fill;
            tb.BackColor = ColInput;
            tb.ForeColor = ColText;
            tb.BorderStyle = BorderStyle.FixedSingle;
            tb.Margin = new Padding(0, 3, 8, 3);
            if (checkExists)
            {
                tb.TextChanged += delegate { RefreshSourceCheck(); };
            }
            grid.Controls.Add(tb, 1, row);

            Button btn = MakeButton("Обзор…", ColBg, ColText, 96, 26);
            btn.Margin = new Padding(0, 2, 0, 2);
            btn.Click += delegate
            {
                using (FolderBrowserDialog dlg = new FolderBrowserDialog())
                {
                    if (Directory.Exists(tb.Text)) dlg.SelectedPath = tb.Text;
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                    {
                        tb.Text = dlg.SelectedPath;
                    }
                }
            };
            grid.Controls.Add(btn, 2, row);
            return tb;
        }

        private void AutoDetectPaths()
        {
            string exeDir = Path.GetDirectoryName(Application.ExecutablePath);
            string candidate = exeDir;
            for (int i = 0; i < 4 && candidate != null; i++)
            {
                if (File.Exists(Path.Combine(candidate, "tools\\build.sh")) &&
                    Directory.Exists(Path.Combine(candidate, "profile\\airootfs")))
                {
                    txtSource.Text = candidate;
                    txtOutput.Text = Path.Combine(candidate, "out");
                    txtVideos.Text = Path.Combine(candidate, "custom_wallpapers");
                    return;
                }
                DirectoryInfo parent = Directory.GetParent(candidate);
                candidate = parent != null ? parent.FullName : null;
            }
            txtSource.Text = exeDir;
            txtOutput.Text = Path.Combine(exeDir, "out");
            txtVideos.Text = Path.Combine(exeDir, "custom_wallpapers");
        }

        private void RefreshSourceCheck()
        {
            bool srcOk = File.Exists(Path.Combine(txtSource.Text, "tools\\build.sh")) &&
                         Directory.Exists(Path.Combine(txtSource.Text, "profile\\airootfs"));
            lblStatusSrc.Text = srcOk
                ? "✓ Исходники PurOS найдены: " + txtSource.Text
                : "⚠ Папка исходников не содержит tools\\build.sh";
            lblStatusSrc.ForeColor = srcOk ? ColOk : ColWarn;
        }

        public static EnvironmentStatus ProbeEnvironment(string srcDir)
        {
            EnvironmentStatus st = new EnvironmentStatus();
            st.SourceValid = !string.IsNullOrEmpty(srcDir) &&
                             File.Exists(Path.Combine(srcDir, "tools\\build.sh")) &&
                             Directory.Exists(Path.Combine(srcDir, "profile\\airootfs"));

            string wslPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");
            if (!File.Exists(wslPath))
            {
                st.WslInstalled = false;
                return st;
            }

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(wslPath, "-l -q");
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                psi.StandardOutputEncoding = Encoding.Unicode;
                using (Process p = Process.Start(psi))
                {
                    string raw = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                    if (p.ExitCode == 0)
                    {
                        st.WslInstalled = true;
                        string[] lines = raw.Replace("\0", "").Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (string line in lines)
                        {
                            string name = line.Trim();
                            if (name.Equals("PurOS-Builder", StringComparison.OrdinalIgnoreCase) ||
                                name.Equals("FurrOS-Builder", StringComparison.OrdinalIgnoreCase) ||
                                name.Equals("archlinux", StringComparison.OrdinalIgnoreCase) ||
                                name.IndexOf("arch", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                st.ArchDistro = name;
                                break;
                            }
                        }
                    }
                    else
                    {
                        ProcessStartInfo psi2 = new ProcessStartInfo(wslPath, "--status");
                        psi2.UseShellExecute = false;
                        psi2.RedirectStandardOutput = true;
                        psi2.RedirectStandardError = true;
                        psi2.CreateNoWindow = true;
                        using (Process p2 = Process.Start(psi2))
                        {
                            p2.WaitForExit(4000);
                            st.WslInstalled = (p2.ExitCode == 0);
                        }
                    }
                }
            }
            catch
            {
                st.WslInstalled = false;
            }
            return st;
        }

        private void RefreshEnvironment()
        {
            RefreshSourceCheck();
            EnvironmentStatus st = ProbeEnvironment(txtSource.Text);
            wslInstalled = st.WslInstalled;
            detectedDistro = st.ArchDistro ?? "";

            if (wslInstalled)
            {
                lblStatusWsl.Text = "✓ Подсистема Windows для Linux (WSL2) активна";
                lblStatusWsl.ForeColor = ColOk;
            }
            else
            {
                lblStatusWsl.Text = "⚠ WSL2 ещё не включена в Windows (нужна для локальной сборки ISO)";
                lblStatusWsl.ForeColor = ColWarn;
            }

            if (!string.IsNullOrEmpty(detectedDistro))
            {
                lblStatusArch.Text = "✓ Сборочная среда Arch Linux готова (" + detectedDistro + ")";
                lblStatusArch.ForeColor = ColOk;
                btnSetupWsl.Visible = false;
            }
            else
            {
                lblStatusArch.Text = "⚠ Контейнер Arch Linux не установлен — нажми кнопку справа для автоустановки";
                lblStatusArch.ForeColor = ColWarn;
                btnSetupWsl.Visible = true;
            }
        }

        private void OnSetupWslClick(object sender, EventArgs e)
        {
            if (isBuilding) return;

            if (!wslInstalled)
            {
                DialogResult res = MessageBox.Show(
                    this,
                    "Для локальной сборки Linux .ISO на Windows нужна подсистема WSL2.\n\n" +
                    "Сейчас выполнится команда администратора:\n" +
                    "wsl.exe --install --no-distribution\n\n" +
                    "После завершения перезагрузи ПК один раз и снова нажми эту кнопку — контейнер Arch скачается автоматически!",
                    "Установка WSL2",
                    MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Information);
                if (res != DialogResult.OK) return;

                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo("wsl.exe", "--install --no-distribution");
                    psi.Verb = "runas";
                    psi.UseShellExecute = true;
                    Process.Start(psi);
                    AppendLog(">>> Запущена установка WSL2. После её окончания перезагрузи компьютер и снова открой программу.");
                }
                catch (Exception ex)
                {
                    AppendLog("Ошибка запуска установки WSL2: " + ex.Message);
                }
                return;
            }

            isBuilding = true;
            btnSetupWsl.Enabled = false;
            btnBuild.Enabled = false;
            progressBar.Style = ProgressBarStyle.Marquee;

            Thread t = new Thread(delegate()
            {
                try
                {
                    string baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PurOS-Builder");
                    string rootfsDir = Path.Combine(baseDir, "rootfs");
                    string wslArchive = Path.Combine(baseDir, "archlinux.wsl");
                    Directory.CreateDirectory(baseDir);
                    Directory.CreateDirectory(rootfsDir);

                    string url = "https://geo.mirror.pkgbuild.com/wsl/latest/archlinux.wsl";
                    AppendLog(">>> Скачиваю официальный сборочный образ Arch Linux WSL (~450 МБ)...");
                    AppendLog("    URL: " + url);

                    ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                    req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    using (Stream net = resp.GetResponseStream())
                    using (FileStream fs = new FileStream(wslArchive, FileMode.Create, FileAccess.Write))
                    {
                        byte[] buf = new byte[1024 * 256];
                        long total = resp.ContentLength;
                        long done = 0;
                        int lastPct = -5;
                        int read;
                        while ((read = net.Read(buf, 0, buf.Length)) > 0)
                        {
                            fs.Write(buf, 0, read);
                            done += read;
                            if (total > 0)
                            {
                                int pct = (int)((done * 100L) / total);
                                if (pct >= lastPct + 10)
                                {
                                    lastPct = pct;
                                    AppendLog(string.Format("    Скачано: {0} МБ / {1} МБ ({2}%)", done / (1024 * 1024), total / (1024 * 1024), pct));
                                }
                            }
                        }
                    }

                    AppendLog(">>> Импортирую сборочную среду PurOS-Builder в WSL2...");
                    int rc = RunProcessStreaming("wsl.exe",
                        string.Format("--import PurOS-Builder \"{0}\" \"{1}\" --version 2", rootfsDir, wslArchive),
                        Encoding.Unicode);
                    if (rc == 0)
                    {
                        try { File.Delete(wslArchive); } catch { }
                        AppendLog("✓ Среда PurOS-Builder развёрнута! Жми «🐾 СОБРАТЬ PUROS (.ISO)».");
                    }
                    else
                    {
                        AppendLog("⚠ wsl --import завершился с кодом " + rc + ".");
                    }
                }
                catch (Exception ex)
                {
                    AppendLog("Ошибка развёртывания Arch WSL: " + ex.Message);
                }
                finally
                {
                    this.BeginInvoke((MethodInvoker)delegate
                    {
                        isBuilding = false;
                        btnSetupWsl.Enabled = true;
                        btnBuild.Enabled = true;
                        progressBar.Style = ProgressBarStyle.Blocks;
                        RefreshEnvironment();
                    });
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        private static string ToWslPath(string winPath)
        {
            string full = Path.GetFullPath(winPath).TrimEnd('\\');
            if (full.Length >= 2 && full[1] == ':')
            {
                char drive = char.ToLowerInvariant(full[0]);
                string rest = full.Substring(2).Replace('\\', '/');
                return "/mnt/" + drive + rest;
            }
            return full.Replace('\\', '/');
        }

        private void OnBuildClick(object sender, EventArgs e)
        {
            if (isBuilding) return;

            RefreshEnvironment();
            if (!File.Exists(Path.Combine(txtSource.Text, "tools\\build.sh")))
            {
                MessageBox.Show(this, "Укажи правильную папку исходников PurOS (где лежит tools\\build.sh).", "Исходники не найдены", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!wslInstalled || string.IsNullOrEmpty(detectedDistro))
            {
                MessageBox.Show(this, "Сначала нажми «⚙ Подготовить WSL2 + Arch» в верхней панели!", "Нужна среда сборки", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string isoName = Regex.Replace(txtIsoName.Text.Trim(), "[^a-zA-Z0-9_-]", "");
            if (string.IsNullOrEmpty(isoName)) isoName = "puros";
            txtIsoName.Text = isoName;

            string srcWin = txtSource.Text;
            string outWin = txtOutput.Text;
            string vidWin = txtVideos.Text;
            Directory.CreateDirectory(outWin);

            string srcWsl = ToWslPath(srcWin);
            string outWsl = ToWslPath(outWin);
            string vidWsl = Directory.Exists(vidWin) ? ToWslPath(vidWin) : "";
            string buildFb = chkFurrBoot.Checked ? "1" : "0";
            string buildAur = chkAurWalls.Checked ? "1" : "0";
            string distro = detectedDistro;

            isBuilding = true;
            btnBuild.Enabled = false;
            btnStop.Enabled = true;
            progressBar.Style = ProgressBarStyle.Marquee;

            AppendLog("================================================================");
            AppendLog(">>> Начинаю сборку образа " + isoName + " в среде WSL2 (" + distro + ")...");
            AppendLog("    Папка вывода: " + outWin);

            string bashCmd = string.Format(
                "set -euo pipefail; " +
                "echo '🐾 Копирую исходники PurOS во внутреннюю ФС Linux (/var/tmp/puros-src)...'; " +
                "rm -rf /var/tmp/puros-src && mkdir -p /var/tmp/puros-src /var/tmp/puros-iso-out; " +
                "tar -C '{0}' --exclude='./.git' --exclude='./out' --exclude='*.iso' --exclude='*.exe' -cf - . | tar -C /var/tmp/puros-src -xf -; " +
                "find /var/tmp/puros-src/tools -type f -exec sed -i 's/\\r$//' {{}} +; " +
                "chmod +x /var/tmp/puros-src/tools/*.sh; " +
                "CUSTOM_ISO_NAME='{1}' BUILD_FURRBOOT='{2}' BUILD_AUR_WALLS='{3}' CUSTOM_VIDEOS_DIR='{4}' OUT_DIR=/var/tmp/puros-iso-out " +
                "bash /var/tmp/puros-src/tools/build.sh /var/tmp/puros-src; " +
                "mkdir -p '{5}'; " +
                "cp -vf /var/tmp/puros-iso-out/*.iso /var/tmp/puros-iso-out/*.sha256 '{5}/'",
                srcWsl.Replace("'", "'\\''"),
                isoName,
                buildFb,
                buildAur,
                string.IsNullOrEmpty(vidWsl) ? "/var/tmp/puros-src/custom_wallpapers" : vidWsl.Replace("'", "'\\''"),
                outWsl.Replace("'", "'\\''")
            );

            Thread t = new Thread(delegate()
            {
                int rc = -1;
                try
                {
                    string args = string.Format("-d {0} -u root -- bash -lc \"{1}\"",
                        distro,
                        bashCmd.Replace("\\", "\\\\").Replace("\"", "\\\""));
                    rc = RunProcessStreaming("wsl.exe", args, Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    AppendLog("Ошибка запуска сборки: " + ex.Message);
                }
                finally
                {
                    this.BeginInvoke((MethodInvoker)delegate
                    {
                        isBuilding = false;
                        btnBuild.Enabled = true;
                        btnStop.Enabled = false;
                        progressBar.Style = ProgressBarStyle.Blocks;
                        if (rc == 0)
                        {
                            progressBar.Value = 100;
                            AppendLog("================================================================");
                            AppendLog("🎉 ГОТОВО! Загрузочный .ISO PurOS собран в: " + outWin);
                            string[] isos = Directory.GetFiles(outWin, "*.iso");
                            if (isos.Length > 0)
                            {
                                Process.Start("explorer.exe", "/select,\"" + isos[0] + "\"");
                            }
                            MessageBox.Show(this, "Образ PurOS .ISO успешно собран!\n\nПапка: " + outWin + "\n\nЗапиши его на флешку через Ventoy или Rufus.", "Сборка завершена 🐾", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                        else
                        {
                            progressBar.Value = 0;
                            AppendLog("⚠ Сборка остановлена или завершилась с кодом: " + rc);
                        }
                    });
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void OnStopClick(object sender, EventArgs e)
        {
            try
            {
                Process p = runningProc;
                if (p != null && !p.HasExited)
                {
                    p.Kill();
                    AppendLog(">>> Сборка остановлена пользователем.");
                }
            }
            catch { }
        }

        private int RunProcessStreaming(string fileName, string arguments, Encoding enc)
        {
            ProcessStartInfo psi = new ProcessStartInfo(fileName, arguments);
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            psi.StandardOutputEncoding = enc;
            psi.StandardErrorEncoding = enc;

            using (Process p = new Process())
            {
                p.StartInfo = psi;
                DataReceivedEventHandler handler = delegate(object s, DataReceivedEventArgs ev)
                {
                    if (ev.Data != null)
                    {
                        string clean = Regex.Replace(ev.Data.Replace("\0", ""), @"\x1B\[[0-9;]*[A-Za-z]", "");
                        if (clean.Trim().Length > 0) AppendLog(clean);
                    }
                };
                p.OutputDataReceived += handler;
                p.ErrorDataReceived += handler;
                runningProc = p;
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                p.WaitForExit();
                runningProc = null;
                return p.ExitCode;
            }
        }

        private void AppendLog(string line)
        {
            if (this.IsDisposed) return;
            if (this.InvokeRequired)
            {
                try
                {
                    this.BeginInvoke((MethodInvoker)delegate { AppendLog(line); });
                }
                catch { }
                return;
            }
            txtLog.AppendText(string.Format("[{0:HH:mm:ss}] {1}{2}", DateTime.Now, line, Environment.NewLine));
            txtLog.SelectionStart = txtLog.TextLength;
            txtLog.ScrollToCaret();
        }

        public class EnvironmentStatus
        {
            public bool SourceValid;
            public bool WslInstalled;
            public string ArchDistro;
        }

        [STAThread]
        public static int Main(string[] args)
        {
            if (args != null && args.Length > 0 && args[0] == "--self-test")
            {
                string dir = Path.GetDirectoryName(Application.ExecutablePath);
                EnvironmentStatus st = ProbeEnvironment(dir);
                Console.WriteLine("PUROS SELF-TEST OK | SourceValid=" + st.SourceValid + " | WslInstalled=" + st.WslInstalled + " | ArchDistro=" + (st.ArchDistro ?? "NONE"));
                return st.SourceValid ? 0 : 1;
            }

            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }
    }
}
