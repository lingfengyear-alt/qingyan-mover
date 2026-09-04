using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Text;
using System.Net.Http.Headers;
using System.Net;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

var baseDirectory = AppContext.BaseDirectory;
// Prevent ChromeDriver/Chrome diagnostic output from keeping a terminal visible.
try { NativeMethods.FreeConsole(); } catch { }
try { Console.OutputEncoding = new UTF8Encoding(false); } catch (IOException) { }
using var singleInstanceMutex = new Mutex(true, @"Local\QingyanMover.SingleInstance", out var isFirstInstance);
using var showExistingInstance = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\QingyanMover.Show", out _);
if (!isFirstInstance)
{
    try
    {
        using var signal = EventWaitHandle.OpenExisting(@"Local\QingyanMover.Show");
        signal.Set();
    }
    catch (WaitHandleCannotBeOpenedException) { }
    return 0;
}
var configPath = GetOption("--config") ?? Path.Combine(baseDirectory, "config.json");
var dryRun = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);
var once = args.Contains("--once", StringComparer.OrdinalIgnoreCase);
var continueTest = args.Contains("--continue-test", StringComparer.OrdinalIgnoreCase);

var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
var config = JsonSerializer.Deserialize<Config>(await File.ReadAllTextAsync(configPath), options)
             ?? throw new InvalidOperationException("配置无效");
var monitorState = new MonitorState(config.Schedule.StartTime, config.Schedule.EndTime, config.Schedule.IntervalMinutes);
if (!string.IsNullOrWhiteSpace(config.AccountsFile))
    config.AccountsFile = ResolvePath(config.AccountsFile, Path.GetDirectoryName(Path.GetFullPath(configPath))!);

async Task RunQueueAsync()
{
    var accounts = config.ExpandAccounts();
    foreach (var account in accounts)
    {
        account.StateFile = ResolvePath(account.StateFile, baseDirectory);
        account.LogFile = ResolvePath(account.LogFile, baseDirectory);
        account.ArtifactDir = ResolvePath(account.ArtifactDir, baseDirectory);
        monitorState.SetAccount(account.Account, account.AdsPower.ProfileName, "执行中", null, account.StateFile);
        var worker = new Worker(account, dryRun, options);
        var result = await worker.RunAsync();
        monitorState.SetAccount(account.Account, account.AdsPower.ProfileName, result == 0 ? "本轮完成" : "执行失败", result == 0 ? null : "请查看账号日志", account.StateFile);
        if (result != 0)
        {
            Console.WriteLine($"账号 {account.Account} 执行失败，已停止本轮后续账号，避免共享 Chrome 登录目录冲突。");
            break;
        }
    }
}

async Task<int> RunContinueTestAsync()
{
    var account = config.ExpandAccounts().FirstOrDefault()
        ?? throw new InvalidOperationException("accounts.csv 中没有可测试账号");
    account.StateFile = ResolvePath(account.StateFile, baseDirectory);
    account.LogFile = ResolvePath(account.LogFile, baseDirectory);
    account.ArtifactDir = ResolvePath(account.ArtifactDir, baseDirectory);
    var worker = new Worker(account, false, options);
    return await worker.RunContinueTestAsync();
}

if (continueTest)
{
    return await RunContinueTestAsync();
}

if (once)
{
    await RunQueueAsync();
    return 0;
}

async Task RunMonitorLoopAsync()
{
    while (true)
    {
        var now = DateTime.Now;
        if (config.Schedule.IsWithinWindow(now))
        {
            monitorState.SetOverall("本轮检查中");
            monitorState.SetSystemError(null);
            try
            {
                await RunQueueAsync();
            }
            catch (Exception ex)
            {
                monitorState.SetOverall("监控异常");
                monitorState.SetSystemError(ex.Message);
            }
            var completedAt = DateTime.Now;
            var nextDelay = config.Schedule.SecondsUntilNextCheck(completedAt);
            var nextCheck = completedAt.AddSeconds(nextDelay);
            monitorState.SetNext(nextCheck);
            monitorState.SetOverall("监控中");
            await WaitWithMonitoringAsync(nextDelay, nextCheck);
        }
        else
        {
            var seconds = config.Schedule.SecondsUntilNextCheck(now);
            var next = now.AddSeconds(seconds);
            monitorState.SetOverall("等待执行时段");
            monitorState.SetNext(next);
            await WaitWithMonitoringAsync(seconds, next);
        }
    }
}

var monitorTask = RunMonitorLoopAsync();
ApplicationConfiguration.Initialize();
using var monitorForm = new MonitorForm(monitorState, config.Schedule, showExistingInstance);
Application.Run(monitorForm);
return 0;

async Task RunDashboardAsync(MonitorState state)
{
    using var listener = new HttpListener();
    listener.Prefixes.Add("http://127.0.0.1:8765/");
    try
    {
        listener.Start();
    }
    catch (HttpListenerException ex)
    {
        Console.WriteLine($"监控界面启动失败：{ex.Message}");
        return;
    }

    while (listener.IsListening)
    {
        HttpListenerContext context;
        try { context = await listener.GetContextAsync(); }
        catch (HttpListenerException) { break; }
        catch (ObjectDisposedException) { break; }

        var path = context.Request.Url?.AbsolutePath ?? "/";
        var payload = path.Equals("/api/status", StringComparison.OrdinalIgnoreCase)
            ? JsonSerializer.Serialize(state.Snapshot(), options)
            : DashboardHtml();
        var bytes = Encoding.UTF8.GetBytes(payload);
        context.Response.ContentType = path.Equals("/api/status", StringComparison.OrdinalIgnoreCase)
            ? "application/json; charset=utf-8" : "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes, 0, bytes.Length);
        context.Response.Close();
    }
}

string DashboardHtml() => """
<!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>抖音账号监控</title>
<style>:root{font-family:Segoe UI,Microsoft YaHei,sans-serif;color:#16202a;background:#f4f6f8}*{box-sizing:border-box}body{margin:0}.top{background:#13232f;color:#fff;padding:22px 28px}.top h1{margin:0 0 8px;font-size:24px}.muted{color:#aebbc4}.wrap{max-width:1180px;margin:24px auto;padding:0 18px}.summary{display:grid;grid-template-columns:repeat(4,1fr);gap:12px;margin-bottom:18px}.metric,.panel{background:#fff;border:1px solid #dce2e7;border-radius:6px;padding:16px}.metric b{display:block;font-size:24px;margin-top:6px}.panel h2{font-size:17px;margin:0 0 14px}.ok{color:#087f5b}.warn{color:#b54708}.bad{color:#c92a2a}.table{width:100%;border-collapse:collapse}.table th,.table td{text-align:left;padding:12px 10px;border-bottom:1px solid #edf0f2;font-size:14px}.table th{color:#64717b;font-weight:500}.dot{display:inline-block;width:9px;height:9px;border-radius:50%;background:#87939d;margin-right:7px}.dot.ok{background:#12a875}.dot.bad{background:#e03131}@media(max-width:760px){.summary{grid-template-columns:repeat(2,1fr)}.table{display:block;overflow:auto;white-space:nowrap}}</style></head>
<body><header class="top"><h1>抖音账号监控</h1><div id="sub" class="muted">正在连接监控服务...</div></header><main class="wrap"><section class="summary"><div class="metric">运行状态<b id="running">--</b></div><div class="metric">时间段<b id="window">--</b></div><div class="metric">轮询间隔<b id="interval">--</b></div><div class="metric">下一次检查<b id="next">--</b></div></section><section class="panel"><h2>账号状态</h2><table class="table"><thead><tr><th>抖音账号</th><th>状态</th><th>最近检查</th><th>视频</th><th>Facebook/错误</th></tr></thead><tbody id="rows"></tbody></table></section></main>
<script>async function refresh(){try{const s=await (await fetch('/api/status',{cache:'no-store'})).json();document.getElementById('sub').textContent='服务正常 · 最后刷新 '+new Date().toLocaleTimeString();document.getElementById('running').textContent=s.running?'监控中':'已停止';document.getElementById('running').className=s.running?'ok':'';document.getElementById('window').textContent=s.start+' - '+s.end;document.getElementById('interval').textContent=s.interval+' 分钟';document.getElementById('next').textContent=s.next||'计算中';document.getElementById('rows').innerHTML=s.accounts.map(a=>'<tr><td>'+e(a.name)+'</td><td><span class="dot '+(a.status.includes('失败')?'bad':a.status==='执行中'?'ok':'')+'"></span>'+e(a.status)+'</td><td>'+e(a.checkedAt||'尚未检查')+'</td><td>'+e(a.video||'--')+'</td><td>'+e(a.error||'--')+'</td></tr>').join('')}catch(x){document.getElementById('sub').textContent='无法连接监控服务';document.getElementById('running').textContent='连接失败'}}function e(x){return String(x??'').replace(/[&<>"']/g,m=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[m]))}refresh();setInterval(refresh,5000);</script></body></html>
""";

async Task WaitWithMonitoringAsync(int seconds, DateTime nextCheck)
{
    var remaining = Math.Max(1, seconds);
    while (remaining > 0)
    {
        var chunk = Math.Min(60, remaining);
        await Task.Delay(TimeSpan.FromSeconds(chunk));
        remaining -= chunk;
        if (remaining > 0)
            Console.WriteLine($"监控运行中，下一次检查：{nextCheck:HH:mm:ss}，还需约 {Math.Ceiling(remaining / 60d):0} 分钟。");
    }
}

string? GetOption(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

string ResolvePath(string path, string root)
    => Path.IsPathRooted(path) ? path : Path.Combine(root, path);

sealed class Worker
{
    private readonly Config _config;
    private readonly bool _dryRun;
    private readonly JsonSerializerOptions _json;
    private readonly Dictionary<string, object?> _state;
    private readonly Random _random = new();
    private ChromeDriver? _chromeDriver;
    private bool _keepChromeOpenOnFailure;

    public Worker(Config config, bool dryRun, JsonSerializerOptions json)
    {
        _config = config;
        _dryRun = dryRun || config.DryRun;
        _json = json;
        _state = LoadState(config.StateFile);
        Directory.CreateDirectory(Path.GetDirectoryName(config.LogFile)!);
    }

    public async Task<int> RunAsync()
    {
        Log($"run started dry_run={_dryRun} account={_config.Account}");
        // Never reset workflow state here. A scheduled run must be able to
        // resume the same video after a download or Facebook step fails.
        Persist(("account", _config.Account), ("facebook_page", _config.FacebookPage));

        if (_dryRun)
        {
            var videoId = ReadString("latest_non_pinned_video_id") ?? "DRY_RUN_VIDEO";
            var title = "模拟视频标题";
            Stage("scan_complete", new { video_id = videoId, pinned_count = 3 });
            Stage("snapany_ready", new { new_tab = true });
            Stage("extraction_complete", new { video_id = videoId, title });
            Stage("player_open", new { quality = "1080p" });
            Stage("video_download_verified", new { artifact = $"douyin_{videoId}.mp4" });
            Stage("cover_download_verified", new { artifact = $"douyin_{videoId}_cover.jpeg" });
            Stage("metadata_saved", new { artifact = $"douyin_{videoId}_title.txt" });
            Stage("adspower_ready", new { dry_run = true });
            Stage("reel_editor_ready", new { page = _config.FacebookPage });
            Stage("media_uploaded", new { dry_run = true });
            Stage("cover_saved", new { dry_run = true });
            Persist(("stage", "awaiting_publish_confirmation"),
                    ("download_status", "verified"),
                    ("facebook_draft_status", "ready_unpublished"),
                    ("error_reason", null));
            Log("run completed awaiting publish confirmation");
            return 0;
        }

        try
        {
            if (!EnsureDouyinReady())
            {
                Persist(("stage", "waiting_for_manual_login"),
                        ("error_reason", "等待用户完成 Chrome 登录"));
                Console.WriteLine();
                Console.WriteLine("请在当前 Chrome 窗口完成抖音登录，完成后回到此窗口按 Enter 继续。");
                Console.ReadLine();
                if (!WaitForDouyinContent(TimeSpan.FromSeconds(30), requireAuthenticatedUi: false))
                {
                    Console.WriteLine("尚未检测到抖音视频内容，程序保持 Chrome 窗口打开并结束本次运行。");
                    Fail("waiting_for_manual_login", "未检测到抖音视频内容");
                    _keepChromeOpenOnFailure = true;
                    return 4;
                }
                Persist(("chrome_login_confirmed", true), ("error_reason", null));
                Stage("snapany_ready", new { login_confirmed = true });
            }
            else
            {
                Persist(("chrome_login_confirmed", true), ("error_reason", null));
                Log("Chrome 抖音登录状态已确认，跳过人工登录等待");
            }

            var latestScan = await Task.Run(ScanDouyin);
            var activeVideoId = ReadString("active_video_id");
            var activeDraftStatus = ReadString("facebook_draft_status");
            var scan = latestScan;
            if (!string.IsNullOrWhiteSpace(activeVideoId) &&
                !string.Equals(activeVideoId, latestScan.VideoId, StringComparison.Ordinal) &&
                !IsFinalFacebookStatus(activeDraftStatus))
            {
                // Finish the previously discovered video before moving on to a
                // newer one. Its title is retained locally once artifacts exist.
                var savedTitle = ReadLocalTitle(activeVideoId) ?? ReadString("active_video_title") ?? latestScan.Title;
                scan = latestScan with { VideoId = activeVideoId, Title = savedTitle };
                Log($"resuming unfinished video before newer discovery: {activeVideoId}");
            }
            Stage("scan_complete", scan);

            var previous = ReadString("last_successfully_processed_video_id");
            var downloadedIds = ReadStringList("downloaded_video_ids");
            var alreadyPublished = string.Equals(activeDraftStatus, "published", StringComparison.OrdinalIgnoreCase);
            var reusableUnpublishedDraft = string.Equals(activeDraftStatus, "ready_unpublished", StringComparison.OrdinalIgnoreCase);
            if ((alreadyPublished || (!_config.AutoPublish && reusableUnpublishedDraft)) &&
                string.Equals(activeVideoId, scan.VideoId, StringComparison.Ordinal))
            {
                Log(alreadyPublished
                    ? $"latest video has already been published, skipping: {scan.VideoId}"
                    : $"latest video already has an unpublished Facebook draft, skipping: {scan.VideoId}");
                return 0;
            }
            Persist(("active_video_id", scan.VideoId),
                    ("active_video_title", scan.Title),
                    ("facebook_reentry_attempts", string.Equals(activeVideoId, scan.VideoId, StringComparison.Ordinal)
                        ? ReadString("facebook_reentry_attempts") ?? "0" : "0"),
                    ("workflow_status", "material_pending"));
            if (string.Equals(previous, scan.VideoId, StringComparison.Ordinal))
                Log($"previously processed video is incomplete, retrying downloads: {scan.VideoId}");

            ArtifactResult artifacts;
            if (HasCompleteArtifacts(scan.VideoId))
            {
                artifacts = BuildArtifactResultFromLocal(scan.VideoId);
                Log($"reusing verified local artifacts for Facebook recovery: {scan.VideoId}");
            }
            else
            {
                Stage("snapany_extracting", new { video_id = scan.VideoId, page = "https://snapany.com/zh" });
                var extraction = await Task.Run(() => ExtractSnapAny(scan));
                Stage("snapany_ready", new { video_id = scan.VideoId, page = "https://snapany.com/zh" });
                Stage("extraction_complete", extraction);
                artifacts = await Task.Run(() => DownloadAndValidate(scan.VideoId, extraction));
                Stage("video_download_verified", artifacts.Video);
                Stage("cover_download_verified", artifacts.Cover);
                Stage("metadata_saved", artifacts.Title);
            }
            CloseGoogleChromeAfterDownload();
            if (!downloadedIds.Contains(scan.VideoId)) downloadedIds.Add(scan.VideoId);
            var retainedDraftStatus = string.Equals(activeVideoId, scan.VideoId, StringComparison.Ordinal)
                ? activeDraftStatus ?? "not_started"
                : "not_started";
            Persist(("downloaded_video_ids", downloadedIds.Distinct().TakeLast(100).ToList()),
                    ("latest_downloaded_video_id", scan.VideoId),
                    ("download_status", "verified"),
                    ("facebook_draft_status", retainedDraftStatus),
                    ("workflow_status", "facebook_pending"));

            var health = await CheckAdsPowerAsync();
            if (!health.Ok)
            {
                Fail("adspower_ready", $"AdsPower unavailable: {health.Reason ?? "no debug port"}");
                return 2;
            }
            Stage("adspower_ready", health);
            var upload = await Task.Run(() => PrepareFacebookDraft(health, artifacts, scan.VideoId));
            Stage("reel_editor_ready", upload.Editor);
            Stage("media_uploaded", upload.Media);
            Stage("cover_saved", upload.Cover);
            var finalDraftStatus = upload.Published ? "published" : "ready_unpublished";
            Persist(("stage", upload.Published ? "published" : "awaiting_publish_confirmation"),
                    ("download_status", "verified"),
                    ("facebook_draft_status", finalDraftStatus),
                    ("last_successfully_processed_video_id", scan.VideoId),
                    ("success_time", DateTime.Now.ToString("O")),
                    ("error_reason", null));
            Log(upload.Published ? "Facebook Reel 已执行最终发布" : "Facebook Reel 草稿已准备完成，程序未点击最终发布按钮");
            return 0;
        }
        catch (Exception ex)
        {
            var stage = ReadString("stage") ?? "unknown";
            if (!_dryRun)
            {
                _keepChromeOpenOnFailure = true;
                Console.WriteLine($"任务在 {stage} 阶段失败，已保留 Chrome 窗口供检查：{ex.Message}");
            }
            Fail(stage, ex.Message);
            return 4;
        }
        finally
        {
            if (!_keepChromeOpenOnFailure)
            {
                try { _chromeDriver?.Quit(); } catch { }
            }
            _chromeDriver = null;
        }
    }

    public async Task<int> RunContinueTestAsync()
    {
        Log("continue-test started: Continue, edit, cover, save, and title only; publish disabled");
        _keepChromeOpenOnFailure = true;
        try
        {
            var health = await CheckAdsPowerAsync();
            if (!health.Ok)
                throw new InvalidOperationException($"AdsPower unavailable: {health.Reason ?? "no debug port"}");
            Log($"continue-test AdsPower ready debug_port={health.DebugPort}");

            if (string.IsNullOrWhiteSpace(health.DebugPort) ||
                string.IsNullOrWhiteSpace(health.WebDriver) || !File.Exists(health.WebDriver))
                throw new InvalidOperationException("AdsPower 未返回可用的调试端口或 ChromeDriver");

            var chromeOptions = new ChromeOptions { DebuggerAddress = $"127.0.0.1:{health.DebugPort}" };
            var service = ChromeDriverService.CreateDefaultService(
                Path.GetDirectoryName(health.WebDriver)!, Path.GetFileName(health.WebDriver));
            _chromeDriver = new ChromeDriver(service, chromeOptions);
            var driver = _chromeDriver;
            ConfigureAdsPowerWindow(driver);
            driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(25);

            var reelUrl = _config.FacebookReelUrl?.Trim();
            if (string.IsNullOrWhiteSpace(reelUrl))
                reelUrl = "https://www.facebook.com/reels/create/?surface=ADDL_PROFILE_PLUS";
            NavigateTo(driver, reelUrl);
            WaitForFacebookUploadPage(driver);

            var videoPath = Directory.Exists(_config.ArtifactDir)
                ? Directory.GetFiles(_config.ArtifactDir, "douyin_*.mp4", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
                : null;
            if (videoPath is null)
                throw new FileNotFoundException("账号目录中没有可用于测试的 douyin_*.mp4 文件");

            var input = WaitForFacebookVideoInput(driver, TimeSpan.FromSeconds(90));
            input.SendKeys(videoPath);
            Log($"continue-test video selected: {videoPath}");
            PauseRandom(3000, 5000);

            Directory.CreateDirectory(_config.ArtifactDir);
            var before = Path.Combine(_config.ArtifactDir, "continue-test-before.png");
            var after = Path.Combine(_config.ArtifactDir, "continue-test-after.png");
            ((ITakesScreenshot)driver).GetScreenshot().SaveAsFile(before);
            Log($"continue-test before screenshot: {before}");
            LogFixedPointDiagnostics(driver, 227, 805, "before");

            ClickFacebookNativeFixedPoint(driver, 227, 805, "test first continue");
            PauseRandom(3000, 5000);
            ((ITakesScreenshot)driver).GetScreenshot().SaveAsFile(after);
            Log($"continue-test after first screenshot: {after}");
            LogFixedPointDiagnostics(driver, 227, 805, "after-first");

            // Test the second Continue independently. It must be resolved
            // after the first modal transition, not reused from stale state.
            ClickFacebookNativeFixedPoint(driver, 227, 805, "test second continue");
            PauseRandom(3000, 5000);
            var afterSecond = Path.Combine(_config.ArtifactDir, "continue-test-after-second.png");
            ((ITakesScreenshot)driver).GetScreenshot().SaveAsFile(afterSecond);
            Log($"continue-test after second screenshot: {afterSecond}");
            LogFixedPointDiagnostics(driver, 227, 805, "after-second");

            var videoId = Path.GetFileNameWithoutExtension(videoPath)
                .Replace("douyin_", "", StringComparison.OrdinalIgnoreCase);
            var coverPath = Path.Combine(_config.ArtifactDir, $"douyin_{videoId}_cover.jpeg");
            var titlePath = Path.Combine(_config.ArtifactDir, $"douyin_{videoId}_title.txt");
            if (!File.Exists(coverPath) || !File.Exists(titlePath))
                throw new FileNotFoundException($"测试素材不完整: {coverPath} / {titlePath}");

            ClickFacebookThumbnailEdit(driver);
            PauseRandom(1500, 2500);
            var thumbnailInput = new WebDriverWait(driver, TimeSpan.FromSeconds(20))
                .Until(FindFacebookDialogFileInput);
            if (thumbnailInput is null)
                throw new InvalidOperationException("测试模式未找到封面上传输入框");
            thumbnailInput.SendKeys(coverPath);
            Log($"continue-test cover selected: {coverPath}");
            PauseRandom(1800, 3200);
            ClickFacebookThumbnailDialogPoint(driver, 800, 710, "test thumbnail save");
            PauseRandom(1500, 2500);

            var title = File.ReadAllText(titlePath, Encoding.UTF8).Trim();
            EnterFacebookReelTitle(driver, title);
            Log($"continue-test title entered length={title.Length}; publish intentionally skipped");
            var finished = Path.Combine(_config.ArtifactDir, "continue-test-finished.png");
            ((ITakesScreenshot)driver).GetScreenshot().SaveAsFile(finished);
            Log($"continue-test finished screenshot: {finished}");
            Log("continue-test finished through title entry; browser kept open for inspection");
            return 0;
        }
        catch (Exception ex)
        {
            Log($"continue-test failed: {ex.Message}");
            Console.WriteLine($"继续按钮测试失败，已保留 AdsPower 窗口：{ex.Message}");
            return 4;
        }
    }

    private void LogFixedPointDiagnostics(IWebDriver driver, int x, int y, string phase)
    {
        try
        {
            var viewport = (string?)((IJavaScriptExecutor)driver).ExecuteScript(
                "return Math.round(innerWidth)+'x'+Math.round(innerHeight);");
            var target = FindFacebookControlAtPoint(driver, x, y);
            var detail = target is null ? "none" : DescribeControl(target);
            Log($"continue-test {phase} fixed point x={x} y={y} viewport={viewport} control={detail}");
        }
        catch (Exception ex)
        {
            Log($"continue-test {phase} diagnostics failed: {ex.Message}");
        }
    }

    private ScanResult ScanDouyin()
    {
        var driver = CreateChromeDriver();
        NavigateTo(driver, _config.DouyinUrl);
        WaitForBody(driver);
        var account = driver.FindElements(By.XPath($"//*[normalize-space(text())={XPathLiteral(_config.Account)}]")).Count > 0;
        // Do not scroll to the document bottom: Douyin appends recommended
        // videos there, which must never be treated as this account's works.
        Thread.Sleep(1200);
        var profileCards = WaitForProfileVideoCards(driver, TimeSpan.FromSeconds(10));
        if (profileCards.Count == 0)
            throw new InvalidOperationException("未在该账号主页的作品列表中读取到视频；已停止以避免误用推荐视频");
        var cards = profileCards
            .Select(x => new
            {
                Href = x.GetAttribute("href") ?? "",
                Text = x.Text ?? ""
            })
            .ToList();
        var pinned = new List<string>();
        foreach (var card in cards)
        {
            var match = Regex.Match(card.Href, @"/video/(\d+)");
            if (!match.Success) continue;
            var id = match.Groups[1].Value;
            if (card.Text.Contains("置顶", StringComparison.Ordinal))
            {
                pinned.Add(id);
            }
        }
        var candidates = cards
            .Select(card => new
            {
                Card = card,
                Match = Regex.Match(card.Href, @"/video/(\d+)")
            })
            .Where(x => x.Match.Success && !x.Card.Text.Contains("置顶", StringComparison.Ordinal))
            .ToList();
        if (candidates.Count == 0) throw new InvalidOperationException("未读取到最新非置顶视频");
        Log($"douyin homepage candidates: {string.Join(", ", candidates.Take(10).Select(x => x.Match.Groups[1].Value))}");
        var latest = candidates[0].Match.Groups[1].Value;
        var title = candidates[0].Card.Text.Replace("\r", " ").Replace("\n", " ").Trim();
        return new ScanResult(_config.DouyinUrl, _config.Account, account, cards.Count, pinned, latest, title);
    }

    private bool EnsureDouyinReady()
    {
        var driver = CreateChromeDriver();
        NavigateTo(driver, _config.DouyinUrl);
        WaitForBody(driver);
        var cookieLoggedIn = HasDouyinLoginCookie(driver);
        var ready = WaitForDouyinContent(TimeSpan.FromSeconds(30));
        var videoCardCount = FindProfileVideoCards(driver).Count;
        var accountFound = driver.FindElements(By.XPath($"//*[normalize-space(text())={XPathLiteral(_config.Account)}]")).Count > 0;
        var pageLoggedIn = HasAuthenticatedDouyinUi(driver);
        Log($"douyin ready check cookie_logged_in={cookieLoggedIn} page_logged_in={pageLoggedIn} account_found={accountFound} video_cards={videoCardCount} ready={ready}");
        return cookieLoggedIn && pageLoggedIn;
    }

    private bool WaitForDouyinContent(TimeSpan timeout, bool requireAuthenticatedUi = true)
    {
        var driver = CreateChromeDriver();
        var end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end)
        {
            var cards = FindProfileVideoCards(driver);
            var body = driver.FindElements(By.TagName("body")).FirstOrDefault()?.Text ?? "";
            var authenticated = HasDouyinLoginCookie(driver) &&
                (!requireAuthenticatedUi || HasAuthenticatedDouyinUi(driver));
            if (authenticated && cards.Count > 0 && body.Length > 100)
                return true;
            Thread.Sleep(500);
        }
        return false;
    }

    private static bool HasDouyinLoginCookie(ChromeDriver driver)
    {
        var loginCookieNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "sessionid", "sessionid_ss", "sid_guard", "sid_tt"
        };
        try
        {
            return driver.Manage().Cookies.AllCookies.Any(cookie =>
                loginCookieNames.Contains(cookie.Name) && !string.IsNullOrWhiteSpace(cookie.Value));
        }
        catch (WebDriverException)
        {
            return false;
        }
    }

    private static List<IWebElement> FindProfileVideoCards(IWebDriver driver)
    {
        // Never fall back to page-wide video links: Douyin's recommendation area uses
        // the same URL shape and must not be treated as this author's works.
        var selectors = new[]
        {
            "[data-e2e='user-post-list'] a[href*='/video/']"
        };
        foreach (var selector in selectors)
        {
            var cards = driver.FindElements(By.CssSelector(selector)).ToList();
            if (cards.Count > 0) return cards;
        }
        return new();
    }

    private static List<IWebElement> WaitForProfileVideoCards(IWebDriver driver, TimeSpan timeout)
    {
        var end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end)
        {
            var cards = FindProfileVideoCards(driver);
            if (cards.Count > 0) return cards;
            Thread.Sleep(300);
        }
        return new();
    }

    private static bool HasAuthenticatedDouyinUi(ChromeDriver driver)
    {
        try
        {
            var visibleLogin = driver.FindElements(By.CssSelector(
                    "[data-e2e*='login'], [data-e2e*='Login'], button, [role='button'], a"))
                .Any(element =>
                {
                    if (!element.Displayed || !element.Enabled) return false;
                    var label = string.Join(" ", element.GetAttribute("data-e2e"), element.Text,
                        element.GetAttribute("aria-label"), element.GetAttribute("title"));
                    return Regex.IsMatch(label, "nav[-_]?login|^\\s*(登录|登入|登陆|Log in|Login)\\s*$", RegexOptions.IgnoreCase);
                });
            if (visibleLogin) return false;

            var body = driver.FindElements(By.TagName("body")).FirstOrDefault()?.Text ?? "";
            var hasUserEntry = driver.FindElements(By.CssSelector(
                    "[data-e2e='nav-avatar'], [data-e2e='nav-user'], [data-e2e='user-info'], [data-e2e*='nav-user'], [aria-label*='个人中心'], [aria-label*='账号中心']"))
                .Any(element => element.Displayed);
            return hasUserEntry || body.Contains("退出登录", StringComparison.Ordinal) || body.Contains("切换账号", StringComparison.Ordinal);
        }
        catch (WebDriverException)
        {
            return false;
        }
    }

    private ExtractionResult ExtractSnapAny(ScanResult scan)
    {
        var downloadDir = Path.Combine(_config.ArtifactDir, "browser-downloads");
        Directory.CreateDirectory(downloadDir);
        var driver = CreateChromeDriver(downloadDir);
        NavigateTo(driver, "https://snapany.com/zh");
        WaitForBody(driver);
        var input = new WebDriverWait(driver, TimeSpan.FromSeconds(20))
            .Until(d => d.FindElement(By.CssSelector("input, textarea")));
        input.Clear();
        input.SendKeys($"https://www.douyin.com/video/{scan.VideoId}");
        var button = driver.FindElements(By.XPath("//*[self::button or self::input][contains(normalize-space(.),'提取视频图片')]"))
            .FirstOrDefault();
        if (button is null) throw new InvalidOperationException("SnapAny 提取按钮未找到");
        button.Click();
        var wait = new WebDriverWait(driver, TimeSpan.FromSeconds(45));
        wait.Until(d => d.FindElements(By.CssSelector("a[href]"))
            .Any(x => (x.Text ?? "").Contains("原画", StringComparison.OrdinalIgnoreCase)
                   || (x.Text ?? "").Contains("1080p", StringComparison.OrdinalIgnoreCase)
                   || (x.Text ?? "").Contains("下载视频", StringComparison.Ordinal)));
        var text = driver.FindElement(By.TagName("body")).Text;
        Log($"snapany page url={driver.Url} title={driver.Title} body={Compact(text, 1200)}");
        var title = text.Split('\n').FirstOrDefault(x => x.Contains("#", StringComparison.Ordinal))?.Trim()
                    ?? scan.Title;
        var links = driver.FindElements(By.CssSelector("a[href]"));
        // Prefer a practical HD file. SnapAny can expose an enormous "原画"
        // file while 720p/540p is the same usable short-video source.
        var videoLink = links
            .Where(x => (x.Text ?? "").Contains("720p", StringComparison.OrdinalIgnoreCase))
            .Concat(links.Where(x => (x.Text ?? "").Contains("540p", StringComparison.OrdinalIgnoreCase)))
            .Concat(links.Where(x => (x.Text ?? "").Contains("原画", StringComparison.OrdinalIgnoreCase)
                                  || (x.Text ?? "").Contains("1080p", StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.GetAttribute("href")));
        var original = videoLink?.GetAttribute("href");
        var cover = links
            .Where(x => (x.Text ?? "").Contains("下载封面", StringComparison.Ordinal))
            .Select(x => x.GetAttribute("href"))
            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        var resultLinks = links
            .Where(x => !string.IsNullOrWhiteSpace(x.GetAttribute("href")))
            .Select(x => $"{x.Text.Trim()} => {x.GetAttribute("href")!.Length} chars")
            .Where(x => x.Contains("mp4", StringComparison.OrdinalIgnoreCase)
                     || x.Contains("原画", StringComparison.OrdinalIgnoreCase)
                     || x.Contains("1080", StringComparison.OrdinalIgnoreCase))
            .Take(8)
            .ToArray();
        Log($"snapany links: {string.Join(" | ", resultLinks)}");
        if (string.IsNullOrWhiteSpace(original)) throw new InvalidOperationException("SnapAny 未返回原画或1080p下载入口");
        if (string.IsNullOrWhiteSpace(cover)) throw new InvalidOperationException("SnapAny 未返回封面下载入口");
        var coverPath = Path.Combine(downloadDir, $"douyin_{scan.VideoId}_cover.jpeg");
        DownloadCoverWithRetry(cover, coverPath);
        return new ExtractionResult(title, original, coverPath, downloadDir);
    }

    private void DownloadCoverWithRetry(string coverUrl, string coverPath)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
                client.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/152 Safari/537.36");
                var bytes = client.GetByteArrayAsync(coverUrl).GetAwaiter().GetResult();
                if (bytes.Length < 1024 || !(bytes[0] == 0xFF && bytes[1] == 0xD8))
                    throw new InvalidDataException("封面响应不是有效 JPEG");
                File.WriteAllBytes(coverPath, bytes);
                Log($"SnapAny cover download verified attempt={attempt} bytes={bytes.Length}");
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException)
            {
                lastError = ex;
                Log($"SnapAny cover download attempt={attempt} failed: {ex.Message}");
                if (attempt < 2) Thread.Sleep(1200);
            }
        }

        throw new TimeoutException("SnapAny 封面下载超时或响应无效，未开始视频下载", lastError);
    }

    private ArtifactResult DownloadAndValidate(string videoId, ExtractionResult extraction)
    {
        var targetDir = _config.ArtifactDir;
        Directory.CreateDirectory(targetDir);
        var videoPath = Path.Combine(targetDir, $"douyin_{videoId}.mp4");
        var titlePath = Path.Combine(targetDir, $"douyin_{videoId}_title.txt");
        var driver = CreateChromeDriver(extraction.DownloadDir);
        var existingVideo = Directory.GetFiles(targetDir, $"douyin_{videoId}*.mp4")
            .FirstOrDefault(IsMp4);
        if (existingVideo is not null)
        {
            Log($"valid video already exists, skip browser download: {existingVideo}");
            if (!string.Equals(existingVideo, videoPath, StringComparison.OrdinalIgnoreCase))
                File.Copy(existingVideo, videoPath, true);
            SaveVideoMetadata(videoId, extraction, videoPath, titlePath);
            return BuildArtifactResult(videoPath, extraction, titlePath, videoId);
        }

        new WebDriverWait(driver, TimeSpan.FromSeconds(20)).Until(d =>
            d.Url.Contains("snapany.com", StringComparison.OrdinalIgnoreCase));
        var originalLink = driver.FindElements(By.CssSelector("a[href]"))
            .FirstOrDefault(x => string.Equals(x.GetAttribute("href"), extraction.OriginalUrl, StringComparison.Ordinal))
            ?? driver.FindElements(By.CssSelector("a[href]"))
            .FirstOrDefault(x => (x.Text ?? "").Contains("原画", StringComparison.OrdinalIgnoreCase))
            ?? driver.FindElements(By.CssSelector("a[href]"))
                .FirstOrDefault(x => (x.Text ?? "").Contains("1080p", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("SnapAny 播放器链接未找到");
        var quality = originalLink.Text.Trim();
        var handlesBefore = driver.WindowHandles.ToHashSet();
        ((IJavaScriptExecutor)driver).ExecuteScript("arguments[0].click();", originalLink);
        new WebDriverWait(driver, TimeSpan.FromSeconds(20))
            .Until(d => d.WindowHandles.Count > handlesBefore.Count || !d.Url.Contains("snapany.com", StringComparison.OrdinalIgnoreCase));
        if (driver.WindowHandles.Count > handlesBefore.Count)
        {
            var playerHandle = SelectNewPlayerWindow(driver, handlesBefore);
            driver.SwitchTo().Window(playerHandle);
        }
        Stage("player_open", new { quality, url = driver.Url, new_window = driver.WindowHandles.Count > handlesBefore.Count });
        var downloaded = DownloadFromVisiblePlayer(driver, extraction.OriginalUrl, extraction.DownloadDir);
        if (!IsMp4(downloaded)) throw new InvalidOperationException("下载结果不是有效 MP4");
        File.Copy(downloaded, videoPath, true);
        SaveVideoMetadata(videoId, extraction, videoPath, titlePath);
        var coverPath = Path.Combine(targetDir, $"douyin_{videoId}_cover.jpeg");
        if (!File.Exists(extraction.CoverPath)) throw new InvalidOperationException("封面文件不存在");
        File.Copy(extraction.CoverPath, coverPath, true);
        if (!IsJpeg(coverPath)) throw new InvalidOperationException("封面不是有效 JPEG");
        return new ArtifactResult(
            new { artifact = videoPath, bytes = new FileInfo(videoPath).Length },
            new { artifact = coverPath, bytes = new FileInfo(coverPath).Length },
            new { artifact = titlePath });
    }

    private static string SelectNewPlayerWindow(ChromeDriver driver, ISet<string> handlesBefore)
    {
        var end = DateTime.UtcNow.AddSeconds(20);
        string? nonBlankCandidate = null;
        while (DateTime.UtcNow < end)
        {
            foreach (var handle in driver.WindowHandles.Where(h => !handlesBefore.Contains(h)))
            {
                try
                {
                    driver.SwitchTo().Window(handle);
                    var url = driver.Url;
                    if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        nonBlankCandidate ??= handle;
                        if (driver.FindElements(By.TagName("video")).Count > 0)
                            return handle;
                    }
                }
                catch (WebDriverException)
                {
                    // A newly created tab can still be transitioning from
                    // about:blank while Chrome initializes the media player.
                }
            }
            Thread.Sleep(300);
        }
        return nonBlankCandidate
            ?? throw new TimeoutException("点击下载分辨率后未找到视频播放器标签页");
    }

    private string DownloadFromVisiblePlayer(ChromeDriver driver, string playerUrl, string downloadDir)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var before = Directory.GetFiles(downloadDir);
            try
            {
                EnsurePlayerOpen(driver, playerUrl);
                var player = new WebDriverWait(driver, TimeSpan.FromSeconds(30))
                    .Until(d => d.FindElements(By.TagName("video")).FirstOrDefault());
                if (player is null) throw new InvalidOperationException("播放器页面未找到 video 元素");

                // Pause first so Chrome keeps the native control bar visible.
                ((IJavaScriptExecutor)driver).ExecuteScript(
                    "arguments[0].pause(); arguments[0].dispatchEvent(new Event('pause'));", player);
                Log("visible player paused before locating menu");

                // Reveal Chrome's native controls before taking the vision frame.
                // This matters for landscape videos because their control bar
                // layout differs from portrait videos and is hidden on idle.
                new OpenQA.Selenium.Interactions.Actions(driver)
                    .MoveToElement(player, 0, 0).Perform();
                Thread.Sleep(900);

                var rect = (Dictionary<string, object>)((IJavaScriptExecutor)driver).ExecuteScript(
                    "const r=arguments[0].getBoundingClientRect(); return {x:r.x,y:r.y,width:r.width,height:r.height};", player)!;
                var left = Convert.ToDouble(rect["x"]!);
                var top = Convert.ToDouble(rect["y"]!);
                var right = left + Convert.ToDouble(rect["width"]!);
                var bottom = top + Convert.ToDouble(rect["height"]!);
                Log($"player rect left={left:0} top={top:0} width={right - left:0} height={bottom - top:0}");

                // Fixed relative positions taken from the supplied landscape
                // player screenshots. Keeping them relative to the video makes
                // the clicks stable when the browser window is resized.
                var menuX = right - 14;
                var menuY = bottom - 28;
                var menuOffsetX = Convert.ToInt32(menuX - left - (right - left) / 2);
                var menuOffsetY = Convert.ToInt32(menuY - top - (bottom - top) / 2);
                Log($"fixed player menu click attempt={attempt} point x={menuX:0} y={menuY:0}");
                new OpenQA.Selenium.Interactions.Actions(driver)
                    .MoveToElement(player, menuOffsetX, menuOffsetY).Click().Perform();
                Log($"fixed player menu click executed attempt={attempt} point x={menuX:0} y={menuY:0}");
                Thread.Sleep(700);

                // The visible menu in the supplied screenshots is anchored to
                // the lower-right of the player; its first row is Download.
                var downloadX = right - 130;
                var downloadY = bottom - 130;
                var downloadOffsetX = Convert.ToInt32(downloadX - left - (right - left) / 2.0);
                var downloadOffsetY = Convert.ToInt32(downloadY - top - (bottom - top) / 2.0);
                new OpenQA.Selenium.Interactions.Actions(driver)
                    .MoveToElement(player, downloadOffsetX, downloadOffsetY)
                    .Click()
                    .Perform();
                Log($"fixed player download clicked attempt={attempt} point x={downloadX:0} y={downloadY:0}");

                try
                {
                        var downloaded = WaitForCompletedFile(downloadDir, before, TimeSpan.FromSeconds(12));
                        if (!IsMp4(downloaded)) throw new InvalidOperationException($"下载结果不是有效 MP4: {downloaded}");
                        Log($"visible player download completed attempt={attempt} file={downloaded} bytes={new FileInfo(downloaded).Length}");
                        return downloaded;
                }
                catch (TimeoutException)
                {
                        try { driver.SwitchTo().ActiveElement().SendKeys(OpenQA.Selenium.Keys.Escape); } catch { }
                        Log($"no download after fixed menu points x={menuX:0},y={menuY:0} download={downloadX:0},{downloadY:0}");
                }

                throw new TimeoutException("播放器三个点菜单点击位置未触发下载");
            }
            catch (Exception ex) when (ex is WebDriverException or TimeoutException or InvalidOperationException)
            {
                lastError = ex;
                Log($"visible player download attempt={attempt} failed: {ex.Message}");
                try { driver.Close(); } catch { }
                try
                {
                    if (driver.WindowHandles.Count > 0)
                        driver.SwitchTo().Window(driver.WindowHandles.Last());
                    NavigateTo(driver, playerUrl);
                }
                catch (Exception reopenError)
                {
                    lastError = reopenError;
                    Log($"player reopen attempt={attempt} failed: {reopenError.Message}");
                }
                Thread.Sleep(800);
            }
        }

        throw new TimeoutException($"播放器下载重试 3 次仍未完成: {lastError?.Message}", lastError);
    }

    private (double x, double y)? LocateThreeDotWithVision(ChromeDriver driver, double left, double top, double right, double bottom)
    {
        var vision = _config.Vision;
        if (vision is null)
            return null;
        var apiKey = !string.IsNullOrWhiteSpace(vision.ApiKey)
            ? vision.ApiKey
            : Environment.GetEnvironmentVariable(vision.ApiKeyEnvironment);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Log("vision locator skipped: api_key and environment variable are both not set");
            return null;
        }

        try
        {
            var playerElement = driver.FindElements(By.TagName("video")).FirstOrDefault();
            if (playerElement is null) return null;
            var image = ((ITakesScreenshot)playerElement).GetScreenshot().AsByteArray;
            var prompt = "这是一个只包含 HTML5 视频播放器区域的截图，播放器可能是横屏或竖屏。请只寻找已经显示在播放器原生控制栏中的右侧‘三个点/更多’按钮。横屏时不要按视频内容中心、播放速度区域或网页右下角推测；必须以截图中实际可见的三个点图标为准。不要寻找网页按钮、下载按钮或地址栏。返回按钮中心在这张播放器截图内的归一化坐标，x和y都必须在0到1之间。只返回JSON：{\"found\":true,\"x\":0.0,\"y\":0.0}；看不到真实三个点返回{\"found\":false}。";
            var payload = new
            {
                model = string.IsNullOrWhiteSpace(vision.Model) ? "gpt-5.6-terra" : vision.Model,
                temperature = 0,
                max_tokens = 100,
                messages = new object[]
                {
                    new { role = "user", content = new object[]
                    {
                        new { type = "text", text = prompt },
                        new { type = "image_url", image_url = new { url = "data:image/png;base64," + Convert.ToBase64String(image) } }
                    }}
                }
            };
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Clamp(vision.TimeoutSeconds, 5, 60)) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            var endpoint = vision.Endpoint.TrimEnd('/');
            using var response = client.PostAsync(endpoint, new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode) { Log($"vision locator API failed status={(int)response.StatusCode}"); return null; }
            using var doc = JsonDocument.Parse(body);
            var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
            var match = Regex.Match(content, "\\{\\s*\\\"found\\\"\\s*:\\s*(true|false)(?:\\s*,\\s*\\\"x\\\"\\s*:\\s*([0-9.]+)\\s*,\\s*\\\"y\\\"\\s*:\\s*([0-9.]+))?", RegexOptions.IgnoreCase);
            if (!match.Success || !bool.Parse(match.Groups[1].Value)) return null;
            if (!double.TryParse(match.Groups[2].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var nx) ||
                !double.TryParse(match.Groups[3].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ny) ||
                nx < 0 || nx > 1 || ny < 0 || ny > 1) return null;
            return (left + (right - left) * nx, top + (bottom - top) * ny);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or WebDriverException)
        {
            Log($"vision locator unavailable: {ex.GetType().Name}");
            return null;
        }
    }

    private (double x, double y)? LocateDownloadWithVision(ChromeDriver driver)
    {
        var vision = _config.Vision;
        if (vision is null) return null;
        var apiKey = !string.IsNullOrWhiteSpace(vision.ApiKey)
            ? vision.ApiKey
            : Environment.GetEnvironmentVariable(vision.ApiKeyEnvironment);
        if (string.IsNullOrWhiteSpace(apiKey)) return null;

        try
        {
            var screenshot = ((ITakesScreenshot)driver).GetScreenshot();
            var image = screenshot.AsByteArray;
            var prompt = "这是打开了 HTML5 视频播放器‘更多/三个点’后的当前浏览器截图。请只寻找菜单中实际可见、文字为‘下载’或 Download 的菜单项。不要返回播放速度、画中画、复制链接或其他菜单项，也不要按菜单顺序猜测。返回该下载菜单项中心在整张截图内的归一化坐标，x和y都必须在0到1之间。只返回JSON：{\"found\":true,\"x\":0.0,\"y\":0.0}；看不到下载菜单项返回{\"found\":false}。";
            var payload = new
            {
                model = string.IsNullOrWhiteSpace(vision.Model) ? "gpt-5.6-terra" : vision.Model,
                temperature = 0,
                max_tokens = 100,
                messages = new object[]
                {
                    new { role = "user", content = new object[]
                    {
                        new { type = "text", text = prompt },
                        new { type = "image_url", image_url = new { url = "data:image/png;base64," + Convert.ToBase64String(image) } }
                    }}
                }
            };
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Clamp(vision.TimeoutSeconds, 5, 60)) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var response = client.PostAsync(vision.Endpoint.TrimEnd('/'),
                new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode) return null;
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(body);
            var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
            var match = Regex.Match(content, "\\{\\s*\\\"found\\\"\\s*:\\s*(true|false)(?:\\s*,\\s*\\\"x\\\"\\s*:\\s*([0-9.]+)\\s*,\\s*\\\"y\\\"\\s*:\\s*([0-9.]+))?", RegexOptions.IgnoreCase);
            if (!match.Success || !bool.Parse(match.Groups[1].Value)) return null;
            if (!double.TryParse(match.Groups[2].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var nx) ||
                !double.TryParse(match.Groups[3].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ny) ||
                nx < 0 || nx > 1 || ny < 0 || ny > 1) return null;
            var size = driver.Manage().Window.Size;
            var point = (x: nx * size.Width, y: ny * size.Height);
            Log($"vision locator proposed download menu point x={point.x:0} y={point.y:0}");
            return point;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or WebDriverException)
        {
            Log($"download vision locator unavailable: {ex.GetType().Name}");
            return null;
        }
    }

    private void EnsurePlayerOpen(ChromeDriver driver, string playerUrl)
    {
        if (driver.WindowHandles.Count == 0)
        {
            NavigateTo(driver, playerUrl);
            return;
        }

        try
        {
            driver.SwitchTo().Window(driver.WindowHandles.Last());
            if (!driver.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                NavigateTo(driver, playerUrl);
        }
        catch (NoSuchWindowException)
        {
            NavigateTo(driver, playerUrl);
        }
    }

    private void SaveVideoMetadata(string videoId, ExtractionResult extraction, string videoPath, string titlePath)
    {
        var info = new FileInfo(videoPath);
        if (!info.Exists || info.Length <= 1024 || !IsMp4(videoPath))
            throw new InvalidOperationException("最终视频文件校验失败");
        File.WriteAllText(titlePath, extraction.Title, Encoding.UTF8);
        Stage("video_download_verified", new { artifact = videoPath, bytes = info.Length, source = "Chrome visible player Download" });
    }

    private ArtifactResult BuildArtifactResult(string videoPath, ExtractionResult extraction, string titlePath, string videoId)
    {
        var coverPath = Path.Combine(_config.ArtifactDir, $"douyin_{videoId}_cover.jpeg");
        if (!File.Exists(extraction.CoverPath)) throw new InvalidOperationException("封面文件不存在");
        File.Copy(extraction.CoverPath, coverPath, true);
        if (!IsJpeg(coverPath)) throw new InvalidOperationException("封面不是有效 JPEG");
        return new ArtifactResult(
            new { artifact = videoPath, bytes = new FileInfo(videoPath).Length },
            new { artifact = coverPath, bytes = new FileInfo(coverPath).Length },
            new { artifact = titlePath });
    }

    private ArtifactResult BuildArtifactResultFromLocal(string videoId)
    {
        if (!HasCompleteArtifacts(videoId))
            throw new InvalidOperationException($"本地素材不完整，无法恢复任务: {videoId}");
        var videoPath = Path.Combine(_config.ArtifactDir, $"douyin_{videoId}.mp4");
        var coverPath = Path.Combine(_config.ArtifactDir, $"douyin_{videoId}_cover.jpeg");
        var titlePath = Path.Combine(_config.ArtifactDir, $"douyin_{videoId}_title.txt");
        return new ArtifactResult(
            new { artifact = videoPath, bytes = new FileInfo(videoPath).Length },
            new { artifact = coverPath, bytes = new FileInfo(coverPath).Length },
            new { artifact = titlePath });
    }

    private string? ReadLocalTitle(string videoId)
    {
        var path = Path.Combine(_config.ArtifactDir, $"douyin_{videoId}_title.txt");
        try
        {
            return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8).Trim() : null;
        }
        catch (IOException) { return null; }
    }

    private bool HasCompleteArtifacts(string videoId)
    {
        var videoPath = Path.Combine(_config.ArtifactDir, $"douyin_{videoId}.mp4");
        var coverPath = Path.Combine(_config.ArtifactDir, $"douyin_{videoId}_cover.jpeg");
        var titlePath = Path.Combine(_config.ArtifactDir, $"douyin_{videoId}_title.txt");
        try
        {
            return IsMp4(videoPath) && IsJpeg(coverPath) &&
                File.Exists(titlePath) && !string.IsNullOrWhiteSpace(File.ReadAllText(titlePath, Encoding.UTF8));
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private ChromeDriver CreateChromeDriver(string? downloadDir = null)
    {
        if (_chromeDriver is not null) return _chromeDriver;
        var options = new ChromeOptions();
        options.AddArgument("--disable-notifications");
        options.AddArgument("--no-first-run");
        options.AddArgument("--disable-popup-blocking");
        options.AddArgument("--disable-logging");
        options.AddArgument("--log-level=3");
        options.PageLoadStrategy = PageLoadStrategy.Eager;
        options.BinaryLocation = FindChromeExecutable();
        var profileRootValue = string.IsNullOrWhiteSpace(_config.ChromeUserDataDir)
            ? _config.LegacyEdgeUserDataDir ?? "chrome-profile"
            : _config.ChromeUserDataDir;
        var profileRoot = Path.IsPathRooted(profileRootValue)
            ? profileRootValue
            : Path.Combine(AppContext.BaseDirectory, profileRootValue);
        Directory.CreateDirectory(profileRoot);
        Log($"starting Google Chrome with profile={profileRoot} directory={_config.ChromeProfileDirectory ?? _config.LegacyEdgeProfileDirectory ?? "Default"}");
        options.AddArgument($"--user-data-dir={profileRoot}");
        options.AddArgument($"--profile-directory={_config.ChromeProfileDirectory ?? _config.LegacyEdgeProfileDirectory ?? "Default"}");
        var actualDownloadDir = downloadDir ?? Path.Combine(_config.ArtifactDir, "browser-downloads");
        Directory.CreateDirectory(actualDownloadDir);
        options.AddUserProfilePreference("download.default_directory", actualDownloadDir);
        options.AddUserProfilePreference("download.prompt_for_download", false);
        var service = ChromeDriverService.CreateDefaultService();
        service.HideCommandPromptWindow = true;
        service.SuppressInitialDiagnosticInformation = true;
        _chromeDriver = new ChromeDriver(service, options);
        _chromeDriver.Manage().Window.Size = new System.Drawing.Size(1280, 1024);
        ApplyFixedViewport(_chromeDriver);
        _chromeDriver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(25);
        return _chromeDriver;
    }

    private void ApplyFixedViewport(ChromeDriver driver)
    {
        const int width = 1280;
        const int height = 720;
        try
        {
            driver.ExecuteCdpCommand("Emulation.setDeviceMetricsOverride", new Dictionary<string, object>
            {
                ["width"] = width,
                ["height"] = height,
                ["deviceScaleFactor"] = 1,
                ["mobile"] = false,
                ["screenWidth"] = width,
                ["screenHeight"] = height
            });
            var actual = (string?)((IJavaScriptExecutor)driver)
                .ExecuteScript("return Math.round(innerWidth)+'x'+Math.round(innerHeight);");
            Log($"fixed web viewport requested={width}x{height} actual={actual}");
            if (!string.Equals(actual, $"{width}x{height}", StringComparison.Ordinal))
                throw new InvalidOperationException($"网页实际视口未锁定为 {width}x{height}，当前为 {actual}");
        }
        catch (Exception ex) when (ex is WebDriverException or InvalidOperationException)
        {
            throw new InvalidOperationException($"无法固定网页实际视口为 {width}x{height}: {ex.Message}", ex);
        }
    }

    private void ConfigureAdsPowerWindow(IWebDriver driver)
    {
        const int width = 1280;
        const int height = 1024;
        try
        {
            driver.Manage().Window.Position = new System.Drawing.Point(0, 0);
            driver.Manage().Window.Size = new System.Drawing.Size(width, height);
            var outer = driver.Manage().Window.Size;
            var position = driver.Manage().Window.Position;
            var viewport = (string?)((IJavaScriptExecutor)driver)
                .ExecuteScript("return Math.round(innerWidth)+'x'+Math.round(innerHeight);");
            Log($"AdsPower window configured requested={width}x{height}@0,0 actual={outer.Width}x{outer.Height}@{position.X},{position.Y} viewport={viewport}");
        }
        catch (WebDriverException ex)
        {
            throw new InvalidOperationException($"无法固定 AdsPower 浏览器窗口为 {width}x{height}: {ex.Message}", ex);
        }
    }

    private void CloseGoogleChromeAfterDownload()
    {
        if (_chromeDriver is null) return;
        try
        {
            Log("Google Chrome 查询和下载已完成，正在关闭浏览器");
            _chromeDriver.Quit();
        }
        catch (Exception ex)
        {
            Log($"关闭 Google Chrome 时出现提示：{ex.Message}");
        }
        finally
        {
            _chromeDriver.Dispose();
            _chromeDriver = null;
        }
    }

    private static string FindChromeExecutable()
    {
        var candidates = new[]
        {
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe"
        };
        var executable = candidates.FirstOrDefault(File.Exists);
        return executable ?? throw new FileNotFoundException("未找到 Google Chrome，请先安装 Chrome");
    }

    private void NavigateTo(IWebDriver driver, string url)
    {
        Log($"navigating to {url}");
        try
        {
            driver.Navigate().GoToUrl(url);
            Log($"navigation ready url={driver.Url}");
        }
        catch (WebDriverException ex) when (
            ex is WebDriverTimeoutException ||
            ex.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
        {
            try { ((IJavaScriptExecutor)driver).ExecuteScript("window.stop();"); } catch { }
            Log($"navigation timed out after 25 seconds, continuing with url={driver.Url}");
        }
    }

    private static void WaitForBody(IWebDriver driver)
        => new WebDriverWait(driver, TimeSpan.FromSeconds(30)).Until(d => d.FindElements(By.TagName("body")).Count > 0);

    private void WaitForFacebookUploadPage(IWebDriver driver)
    {
        try
        {
            new WebDriverWait(driver, TimeSpan.FromSeconds(15)).Until(d =>
                d.FindElements(By.TagName("body")).Count > 0);
        }
        catch (WebDriverTimeoutException)
        {
            // Facebook can keep the document load pending while the React
            // upload surface is already usable. Continue with control polling.
        }

        try
        {
            var url = driver.Url;
            Log($"Facebook upload page available for control polling: {url}");
        }
        catch (WebDriverException ex)
        {
            throw new InvalidOperationException($"Facebook 页面无法连接: {ex.Message}", ex);
        }
    }

    private IWebElement WaitForFacebookVideoInput(IWebDriver driver, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var logged = false;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var fileInputs = driver.FindElements(By.CssSelector("input[type=file]"));
                var input = fileInputs
                    .Where(x =>
                    {
                        if (!x.Enabled) return false;
                        var accept = (x.GetAttribute("accept") ?? "").Trim();
                        return string.IsNullOrWhiteSpace(accept) ||
                            accept.Contains("video", StringComparison.OrdinalIgnoreCase) ||
                            accept.Contains("mp4", StringComparison.OrdinalIgnoreCase);
                    })
                    .FirstOrDefault();
                if (input is null && fileInputs.Count == 1 && fileInputs[0].Enabled)
                    input = fileInputs[0];
                if (input is not null)
                {
                    Log($"Facebook video upload input is ready accept={input.GetAttribute("accept") ?? ""}");
                    return input;
                }

                if (!logged)
                {
                    Log("Facebook video upload input not ready; continuing to poll");
                    logged = true;
                }
            }
            catch (StaleElementReferenceException)
            {
                // React may replace the upload control while the page settles.
            }
            catch (WebDriverException ex)
            {
                Log($"Facebook upload control polling retry: {ex.Message}");
            }

            Thread.Sleep(1000);
        }

        throw new TimeoutException(
            $"Facebook Reel 视频上传输入框在 {timeout.TotalSeconds:0} 秒内未出现，当前页面={driver.Url}");
    }

    private static bool HasFacebookUnavailablePage(IWebDriver driver)
    {
        try
        {
            var body = driver.FindElements(By.TagName("body")).FirstOrDefault();
            if (body is null) return false;
            var text = body.Text ?? "";
            return text.Contains("This page isn't available", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("页面不可用", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("此页面无法使用", StringComparison.OrdinalIgnoreCase);
        }
        catch (WebDriverException)
        {
            return false;
        }
    }

    private static string WaitForCompletedFile(string dir, string[] before, TimeSpan timeout)
    {
        var end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end)
        {
            var files = Directory.GetFiles(dir)
                .Where(x => !before.Contains(x, StringComparer.OrdinalIgnoreCase))
                .Where(x => !x.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase))
                .Where(x => Path.GetExtension(x).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var file = files.OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (file is not null && new FileInfo(file).Length > 1024)
            {
                var first = new FileInfo(file).Length;
                Thread.Sleep(800);
                var second = new FileInfo(file).Length;
                if (first == second) return file;
            }
            Thread.Sleep(500);
        }
        throw new TimeoutException("浏览器下载在规定时间内未完成");
    }

    private static bool IsMp4(string path)
    {
        var b = File.ReadAllBytes(path).Take(32).ToArray();
        return b.Length >= 12 && Encoding.ASCII.GetString(b, 4, 4) == "ftyp";
    }

    private static bool IsJpeg(string path)
    {
        using var stream = File.OpenRead(path);
        return stream.ReadByte() == 0xFF && stream.ReadByte() == 0xD8;
    }

    private static string XPathLiteral(string value)
        => value.Contains("'") ? $"concat('{value.Replace("'", "',\"'\",'")}')" : $"'{value}'";

    private static string Compact(string value, int max)
        => Regex.Replace(value.Replace("\r", " ").Replace("\n", " ").Trim(), @"\s+", " ")
            [..Math.Min(max, Regex.Replace(value.Replace("\r", " ").Replace("\n", " ").Trim(), @"\s+", " ").Length)];

    private async Task<AdsPowerHealth> CheckAdsPowerAsync()
    {
        var url = $"{_config.AdsPower.ApiBase.TrimEnd('/')}/api/v1/browser/start?user_id={Uri.EscapeDataString(_config.AdsPower.UserId)}";
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await client.GetAsync(url);
            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var code = doc.RootElement.GetProperty("code").GetInt32();
            var data = doc.RootElement.GetProperty("data");
            var debugPort = data.TryGetProperty("debug_port", out var port) ? port.GetString() : null;
            var webdriver = data.TryGetProperty("webdriver", out var wd) ? wd.GetString() : null;
            var ok = code == 0 && !string.IsNullOrWhiteSpace(debugPort);
            return new AdsPowerHealth(ok, ok ? null : body, debugPort, webdriver);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new AdsPowerHealth(false, ex.Message, null, null);
        }
    }

    private FacebookUploadResult PrepareFacebookDraft(AdsPowerHealth health, ArtifactResult artifacts, string videoId)
    {
        if (string.IsNullOrWhiteSpace(health.DebugPort))
            throw new InvalidOperationException("AdsPower 未返回 debug_port");

        var chromeOptions = new ChromeOptions { DebuggerAddress = $"127.0.0.1:{health.DebugPort}" };
        if (string.IsNullOrWhiteSpace(health.WebDriver) || !File.Exists(health.WebDriver))
            throw new InvalidOperationException("AdsPower 未返回可用的匹配 ChromeDriver");
        var driverService = ChromeDriverService.CreateDefaultService(
            Path.GetDirectoryName(health.WebDriver)!,
            Path.GetFileName(health.WebDriver));
        driverService.HideCommandPromptWindow = true;
        driverService.SuppressInitialDiagnosticInformation = true;
        using var driver = new ChromeDriver(driverService, chromeOptions);
        // Keep the AdsPower outer browser at a stable desktop position and
        // size. CDP metrics are intentionally not used here: Facebook can
        // reset them after navigation, while the outer window remains stable.
        ConfigureAdsPowerWindow(driver);
        var wait = new WebDriverWait(driver, TimeSpan.FromSeconds(20));
        // The private Reel editor cannot be restored from its public /reel/{id}
        // URL. Preserve and reclaim the actual AdsPower browser tab instead.
        var resumeExistingDraft = TrySwitchToFacebookEditorTab(driver);
        if (resumeExistingDraft)
        {
            Log($"Facebook resuming existing editor tab: {driver.Url}");
            ConfigureAdsPowerWindow(driver);
            var needsResumeContinue = !HasFacebookEditorTextSurface(driver);
            if (needsResumeContinue)
            {
                // A saved recovery state can point to an upload screen as well
                // as the editor. Treat its visible controls as authoritative.
                ClickFacebookFirstContinueWithRetry(driver);
                PauseRandom(1800, 3200);
                ClickFacebookContinueAtFixedPosition(driver, "second-resume");
                new WebDriverWait(driver, TimeSpan.FromSeconds(20)).Until(d =>
                    HasFacebookEditorTextSurface(d));
            }
            Persist(("facebook_draft_status", "editor_ready"), ("workflow_status", "facebook_editor_ready"));
        }
        else
        {
            // Use a new tab so an interrupted editor in this AdsPower profile
            // is never overwritten by a homepage or create-page navigation.
            driver.SwitchTo().NewWindow(WindowType.Tab);
            driver.Navigate().GoToUrl("https://www.facebook.com/");
            WaitForBody(driver);
            var bodyText = driver.FindElement(By.TagName("body")).Text ?? "";
            if (bodyText.Contains("登录", StringComparison.OrdinalIgnoreCase) &&
                !bodyText.Contains("退出", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("AdsPower 中 Facebook 尚未登录");
            var reelUrl = _config.FacebookReelUrl?.Trim();
            if (string.IsNullOrWhiteSpace(reelUrl))
                reelUrl = "https://www.facebook.com/reels/create/?surface=ADDL_PROFILE_PLUS";
            Log($"Facebook opening Reel upload entry directly: {reelUrl}");
            NavigateTo(driver, reelUrl);
            WaitForFacebookUploadPage(driver);
            ConfigureAdsPowerWindow(driver);
            if (HasFacebookUnavailablePage(driver))
            {
                var reentryAttempts = ReadInt("facebook_reentry_attempts");
                if (reentryAttempts >= 1)
                    throw new InvalidOperationException("Facebook Reel 页面不可用，已达到重新进入上限（1 次）");
                Persist(("facebook_reentry_attempts", reentryAttempts + 1));
                Log($"Facebook Reel 页面不可用，重新进入创建页 attempt={reentryAttempts + 1}/1");
                NavigateTo(driver, reelUrl);
                WaitForFacebookUploadPage(driver);
                ConfigureAdsPowerWindow(driver);
                if (HasFacebookUnavailablePage(driver))
                    throw new InvalidOperationException("Facebook Reel 创建页重试后仍不可用");
            }

            var videoInput = WaitForFacebookVideoInput(driver, TimeSpan.FromSeconds(90));
            videoInput.SendKeys(GetArtifactPath(artifacts.Video));
            PauseRandom(3000, 7000);
            ClickFacebookFirstContinueWithRetry(driver);
            PauseRandom(1800, 3200);
            ClickFacebookContinueAtFixedPosition(driver, "second");
            // The next screen is handled by the fixed edit/upload sequence.
            // Do not inspect localized text or click Continue a third time.
            PauseRandom(2500, 4500);
            Persist(("facebook_draft_video_id", videoId),
                    ("facebook_draft_url", driver.Url),
                    ("facebook_draft_status", "editor_ready"),
                    ("workflow_status", "facebook_editor_ready"));
        }

        Stage("reel_editor_ready", new { page = _config.FacebookPage, video_id = videoId, url = driver.Url, resumed = resumeExistingDraft });

        var coverPath = GetArtifactPath(artifacts.Cover);
        // Reel settings: edit thumbnail -> upload own thumbnail -> save.
        // Facebook replaces the settings dialog DOM several times while the
        // preview is loading. Keep this wait entirely in JS so no stale
        // IWebElement is retained between refreshes.
        if (!string.Equals(ReadString("facebook_draft_status"), "thumbnail_saved", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(ReadString("facebook_draft_status"), "title_saved", StringComparison.OrdinalIgnoreCase))
        {
            ClickFacebookThumbnailEdit(driver);
            var thumbnailInput = wait.Until(FindFacebookDialogFileInput);
            if (thumbnailInput is null) throw new InvalidOperationException("Facebook 缩略图上传输入框未找到");
            thumbnailInput.SendKeys(coverPath);
            PauseRandom(1800, 3200);
            ClickFacebookThumbnailDialogPoint(driver, 800, 710, "thumbnail save");
            PauseRandom(1200, 2200);
            Persist(("facebook_draft_status", "thumbnail_saved"), ("workflow_status", "facebook_thumbnail_saved"));
            Log($"Facebook thumbnail saved by fixed points: {coverPath}");
        }

        var title = File.ReadAllText(GetArtifactPath(artifacts.Title), Encoding.UTF8).Trim();
        if (!string.Equals(ReadString("facebook_draft_status"), "title_saved", StringComparison.OrdinalIgnoreCase))
        {
            EnterFacebookReelTitle(driver, title);
            Persist(("facebook_draft_status", "title_saved"), ("workflow_status", "facebook_title_saved"));
        }

        var published = false;
        if (_config.AutoPublish)
        {
            ClickFacebookFinalPublish(driver);
            published = true;
            Persist(("facebook_draft_status", "published"), ("workflow_status", "facebook_publish_clicked"));
        }

        return new FacebookUploadResult(
            new { page = _config.FacebookPage, url = driver.Url },
            new { video = GetArtifactPath(artifacts.Video), title = title },
            new { cover = coverPath, status = "已编辑、上传并储存" },
            published);
    }

    private static IWebElement? FindFacebookCreateButton(IWebDriver driver)
    {
        var candidates = driver.FindElements(By.CssSelector("button, [role='button']"))
            .Where(x => x.Displayed && x.Enabled)
            .Where(x => !IsInsideHiddenOrDialog(x));
        return candidates.FirstOrDefault(x =>
            ContainsAccessibleLabel(x, "创建", "創建", "建立", "Create") &&
            !ContainsAccessibleLabel(x, "创建广告", "創建廣告", "建立廣告", "Create ad", "Promote", "推广", "推廣"));
    }

    private static IWebElement? FindFacebookDirectReelEntry(IWebDriver driver)
        => driver.FindElements(By.CssSelector("button, [role='button'], a"))
            .FirstOrDefault(x => x.Displayed && x.Enabled &&
                ContainsAccessibleLabel(x, "创建 Reel", "创建短视频", "創建 Reel", "建立 Reel", "建立 Reels", "Create reel"));

    private static IWebElement? FindFacebookCreateReelItem(IWebDriver driver)
    {
        var candidates = driver.FindElements(By.CssSelector(
                "[role='menuitem'], [role='option'], button, [role='button']"))
            .Where(x => x.Displayed && x.Enabled)
            .Where(x => IsInVisibleMenu(x));
        return candidates.FirstOrDefault(x => HasExactOrAccessibleLabel(
            x, "Reel", "Reels", "短视频", "视频短片", "短片", "创建短视频", "建立短片", "Create reel"));
    }

    private static IWebElement? FindFacebookActionButton(IWebDriver driver, params string[] labels)
        => driver.FindElements(By.CssSelector("button, [role='button']"))
            .FirstOrDefault(x => x.Displayed && x.Enabled &&
                HasExactOrAccessibleLabel(x, labels));

    private void ClickFacebookControl(IWebDriver driver, IWebElement element)
    {
        ((IJavaScriptExecutor)driver).ExecuteScript(
            "arguments[0].scrollIntoView({block:'center', inline:'center'});", element);
        try
        {
            element.Click();
        }
        catch (ElementClickInterceptedException)
        {
            // Facebook can leave a transparent upload overlay above the enabled
            // button for a short time; click the already verified control node.
            ((IJavaScriptExecutor)driver).ExecuteScript("arguments[0].click();", element);
            Log($"Facebook control clicked through transient overlay: {DescribeControl(element)}");
        }
    }

    private void ClickFacebookContinueAtFixedPosition(IWebDriver driver, string step)
    {
        // This is a browser-content coordinate, not a screen coordinate.
        // With the AdsPower window anchored at (0,0) and sized to 1280x1024,
        // the button is near the lower-left edge of the content viewport.
        ClickFacebookNativeFixedPoint(driver, 227, 805, $"{step} continue");
    }

    private void ClickFacebookFirstContinueWithRetry(IWebDriver driver)
    {
        // Facebook shows a second Continue before the editor fields exist.
        // Do not wait for a textbox after the first click; the caller handles
        // the second fixed-position click next.
        ClickFacebookNativeFixedPoint(driver, 227, 805, "first continue");
        PauseRandom(2200, 4200);
        Log("Facebook first Continue finished; moving to second Continue");
    }

    private static bool HasFacebookReelSettingsText(IWebDriver driver)
    {
        try
        {
            return driver.FindElements(By.CssSelector("[role=dialog], [role=main], body"))
                .Any(x => x.Displayed && Regex.IsMatch(
                    x.Text ?? "", "Reel\\s*(設定|设置|settings)|編輯 Reel|编辑 Reel|edit reel",
                    RegexOptions.IgnoreCase));
        }
        catch (WebDriverException)
        {
            return false;
        }
    }

    private static string ReadFacebookUploadCheckState(IWebDriver driver)
    {
        try
        {
            return (string?)((IJavaScriptExecutor)driver).ExecuteScript(@"
const nodes=[...document.querySelectorAll('[role=status],[aria-live],div,span')];
return nodes.map(n=>(n.innerText||n.textContent||'').trim())
  .filter(t=>/检查|檢查|checking|copyright|版權/i.test(t))
  .filter(t=>t.length>0 && t.length<180).join(' | ');") ?? "";
        }
        catch (WebDriverException)
        {
            return "";
        }
    }

    private void ClickFacebookPrimaryButton(IWebDriver driver, IWebElement button, string step)
    {
        var description = DescribeControl(button);
        var location = button.Location;
        var size = button.Size;
        new OpenQA.Selenium.Interactions.Actions(driver)
            .MoveToElement(button)
            .Click()
            .Perform();
        Log($"Facebook native {step} continue clicked: {description} location={location.X},{location.Y} size={size.Width}x{size.Height}");
    }

    private void ClickFacebookFinalPublish(IWebDriver driver)
    {
        // This workflow is calibrated to the fixed 1280x1024 AdsPower
        // window. Do not use button text or responsive coordinates here.
        const int fixedX = 227;
        const int fixedY = 805;
        var result = new WebDriverWait(driver, TimeSpan.FromSeconds(30)).Until(d =>
        {
            try
            {
                return (string?)((IJavaScriptExecutor)d).ExecuteScript(@"
const x=arguments[0], y=arguments[1];
const raw=document.elementFromPoint(x,y);
const el=raw?.closest('button,[role=button],a');
if(!raw || !el) return null;
const r=el.getBoundingClientRect(), s=getComputedStyle(el);
if(r.width<220 || r.height<32 || s.display==='none' || s.visibility==='hidden') return null;
['pointerdown','mousedown','pointerup','mouseup','click'].forEach(type=>
  el.dispatchEvent(new MouseEvent(type,{bubbles:true,cancelable:true,view:window,clientX:x,clientY:y})));
return el.tagName+'@'+Math.round(r.left)+','+Math.round(r.top)+' '+
  (el.getAttribute('aria-label')||el.textContent||'').trim().slice(0,80);", fixedX, fixedY);
            }
            catch (WebDriverException) { return null; }
        });
        Log($"Facebook final fixed coordinate click x={fixedX} y={fixedY} hit={Normalize(result)} viewport=" +
            ((string?)((IJavaScriptExecutor)driver).ExecuteScript("return Math.round(innerWidth)+'x'+Math.round(innerHeight);")));
    }

    private static IWebElement? FindFacebookResumePrimaryButton(IWebDriver driver)
    {
        const string script = @"
const candidates=[...document.querySelectorAll('button,[role=button]')].filter(el=>{
  const r=el.getBoundingClientRect(), s=getComputedStyle(el);
  return r.width>=180 && r.height>=32 && r.left<430 && r.top>0 &&
    s.visibility!=='hidden' && s.display!=='none' && s.pointerEvents!=='none' &&
    !el.disabled && el.getAttribute('aria-disabled')!=='true';
});
const blue=el=>{const c=getComputedStyle(el).backgroundColor.match(/\d+/g)||[];return c.length>=3&&+c[2]>150&&+c[0]<100&&+c[1]<170;};
return candidates.sort((a,b)=>{
  const ar=a.getBoundingClientRect(),br=b.getBoundingClientRect();
  return ((blue(b)?10000:0)+br.width+br.top)-((blue(a)?10000:0)+ar.width+ar.top);
})[0]||null;";
        return (IWebElement?)((IJavaScriptExecutor)driver).ExecuteScript(script);
    }

    private static bool TrySwitchToFacebookEditorTab(IWebDriver driver)
    {
        foreach (var handle in driver.WindowHandles)
        {
            try
            {
                driver.SwitchTo().Window(handle);
                if (!driver.Url.Contains("facebook.com", StringComparison.OrdinalIgnoreCase)) continue;
                if (FindFacebookResumePrimaryButton(driver) is not null) return true;
                if (HasFacebookEditorTextSurface(driver)) return true;
            }
            catch (WebDriverException)
            {
                // Tabs can disappear while Facebook replaces its editor page.
            }
        }
        return false;
    }

    private static bool HasFacebookEditorTextSurface(IWebDriver driver)
        => driver.FindElements(By.CssSelector("[contenteditable=true], textarea, [role=textbox]"))
            .Any(x =>
            {
                try
                {
                    var r = x.Location;
                    var s = x.Size;
                    return x.Displayed && x.Enabled && s.Width > 100 && s.Height > 20 &&
                        r.X < 420 && r.Y < 420;
                }
                catch (StaleElementReferenceException) { return false; }
            });

    private static bool IsFinalFacebookStatus(string? value)
        => string.Equals(value, "ready_unpublished", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "published", StringComparison.OrdinalIgnoreCase);

    private static IWebElement? FindFacebookLowerLeftPrimaryButton(IWebDriver driver)
    {
        const string script = @"
const viewH=window.innerHeight, viewW=window.innerWidth;
const candidates=[...document.querySelectorAll('button,[role=button]')].filter(el=>{
  const r=el.getBoundingClientRect(), s=getComputedStyle(el);
  if (r.width<160 || r.height<32 || r.left>viewW*.48 || r.bottom<viewH-210) return false;
  if (s.visibility==='hidden' || s.display==='none' || s.pointerEvents==='none') return false;
  return !el.disabled && el.getAttribute('aria-disabled')!=='true';
});
const blue=el=>{const c=getComputedStyle(el).backgroundColor.match(/\d+/g)||[];return c.length>=3&&+c[2]>150&&+c[0]<80&&+c[1]<150;};
return candidates.sort((a,b)=>{
  const ar=a.getBoundingClientRect(), br=b.getBoundingClientRect();
  const as=(blue(a)?10000:0)+ar.width*2+ar.bottom;
  const bs=(blue(b)?10000:0)+br.width*2+br.bottom;
  return bs-as;
})[0]||null;";
        return (IWebElement?)((IJavaScriptExecutor)driver).ExecuteScript(script);
    }

    private void ClickFacebookNativeFixedPoint(IWebDriver driver, int fixedX, int fixedY, string action)
    {
        // Keep the horizontal anchor fixed. Facebook docks Continue at the
        // bottom of the modal, so its Y coordinate changes with the viewport.
        var target = new WebDriverWait(driver, TimeSpan.FromSeconds(15)).Until(d =>
            FindFacebookControlAtPoint(d, fixedX, fixedY) ?? FindFacebookLowerLeftPrimaryButton(d));
        var location = target.Location;
        var size = target.Size;
        try
        {
            target.Click();
        }
        catch (WebDriverException)
        {
            target = FindFacebookControlAtPoint(driver, fixedX, fixedY)
                ?? FindFacebookLowerLeftPrimaryButton(driver)
                ?? throw new InvalidOperationException($"Facebook 固定坐标控件失效: {fixedX},{fixedY}");
            new OpenQA.Selenium.Interactions.Actions(driver)
                .MoveToElement(target)
                .Click()
                .Perform();
        }
        var viewport = (string?)((IJavaScriptExecutor)driver).ExecuteScript(
            "return Math.round(innerWidth) + 'x' + Math.round(innerHeight);");
        Log($"Facebook fixed-bottom {action} click anchorX={fixedX} requestedY={fixedY} " +
            $"actual={location.X + size.Width / 2},{location.Y + size.Height / 2} viewport={viewport} " +
            $"control={location.X},{location.Y} size={size.Width}x{size.Height}");
    }

    private void ClickFacebookTrustedViewportPoint(IWebDriver driver, double xRatio, double yRatio, string action)
    {
        var width = Convert.ToInt32(((IJavaScriptExecutor)driver)
            .ExecuteScript("return Math.round(innerWidth);")!);
        var height = Convert.ToInt32(((IJavaScriptExecutor)driver)
            .ExecuteScript("return Math.round(innerHeight);")!);
        var x = Math.Clamp((int)Math.Round(width * xRatio), 1, Math.Max(1, width - 1));
        var y = Math.Clamp((int)Math.Round(height * yRatio), 1, Math.Max(1, height - 1));
        var wait = new WebDriverWait(driver, TimeSpan.FromSeconds(30));
        var target = wait.Until(d => FindFacebookControlAtPoint(d, x, y));
        if (target is null)
            throw new InvalidOperationException($"Facebook 固定点未命中可点击控件: {x},{y} viewport={width}x{height}");

        var location = target.Location;
        var size = target.Size;
        var description = DescribeControl(target);
        try
        {
            target.Click();
        }
        catch (WebDriverException)
        {
            // Re-read the element after a modal repaint and use a trusted
            // pointer action. Do not fall back to synthetic DOM events.
            target = FindFacebookControlAtPoint(driver, x, y)
                ?? throw new InvalidOperationException($"Facebook 固定点控件在点击前失效: {x},{y}");
            new OpenQA.Selenium.Interactions.Actions(driver)
                .MoveToElement(target)
                .Click()
                .Perform();
        }

        Log($"Facebook trusted fixed {action} click x={x} y={y} viewport={width}x{height} " +
            $"control={location.X},{location.Y} size={size.Width}x{size.Height} {description}");
    }

    private static IWebElement? FindFacebookControlAtPoint(IWebDriver driver, int x, int y)
    {
        const string script = @"
const raw=document.elementFromPoint(arguments[0],arguments[1]);
if(!raw) return null;
const el=raw.closest('button,[role=button],a');
if(!el) return null;
const r=el.getBoundingClientRect(), s=getComputedStyle(el);
if(r.width<40 || r.height<20 || r.right<=0 || r.bottom<=0 ||
   s.visibility==='hidden' || s.display==='none' || s.pointerEvents==='none' ||
   el.disabled || el.getAttribute('aria-disabled')==='true') return null;
return el;";
        try
        {
            return (IWebElement?)((IJavaScriptExecutor)driver).ExecuteScript(script, x, y);
        }
        catch (WebDriverException)
        {
            return null;
        }
    }

    private void ClickFacebookNativeFixedViewportPoint(IWebDriver driver, string action)
    {
        // AdsPower may clamp the outer window to the available desktop size.
        // Use one fixed point in the upload panel's viewport proportions so
        // the click remains stable when the actual viewport is resized.
        var width = Convert.ToDouble(((IJavaScriptExecutor)driver)
            .ExecuteScript("return Math.round(innerWidth);")!);
        var height = Convert.ToDouble(((IJavaScriptExecutor)driver)
            .ExecuteScript("return Math.round(innerHeight);")!);
        var x = (int)Math.Round(width * 0.18);
        var y = (int)Math.Round(height * 0.82);
        ClickFacebookNativeFixedPoint(driver, x, y, action);
    }

    private void ClickFacebookScaledPoint(IWebDriver driver, int referenceX, int referenceY, string action)
    {
        var x = GetScaledX(driver, referenceX);
        var y = GetScaledY(driver, referenceY);
        ClickFacebookNativeFixedPoint(driver, x, y, action);
    }

    private static int GetScaledX(IWebDriver driver, int referenceX)
    {
        var width = Convert.ToInt32(((IJavaScriptExecutor)driver)
            .ExecuteScript("return Math.round(innerWidth);")!);
        return (int)Math.Round(referenceX * width / 1188.0);
    }

    private static int GetScaledY(IWebDriver driver, int referenceY)
    {
        var height = Convert.ToInt32(((IJavaScriptExecutor)driver)
            .ExecuteScript("return Math.round(innerHeight);")!);
        return (int)Math.Round(referenceY * height / 826.0);
    }

    private void ClickFacebookScaledDomPoint(IWebDriver driver, int referenceX, int referenceY, string action)
    {
        ClickFacebookFixedPoint(driver, GetScaledX(driver, referenceX), GetScaledY(driver, referenceY), action);
    }

    private static IWebElement? FindFacebookDialogFileInput(IWebDriver driver)
    {
        var dialog = driver.FindElements(By.CssSelector("[role='dialog']"))
            .Where(x => x.Displayed)
            .LastOrDefault();
        var inputs = (dialog?.FindElements(By.CssSelector("input[type=file]"))
            ?? driver.FindElements(By.CssSelector("input[type=file]")))
            .ToList();
        var imageInput = inputs.LastOrDefault(x =>
        {
            try
            {
                if (!(x.Displayed || x.Enabled)) return false;
                var accept = (x.GetAttribute("accept") ?? "").Trim();
                return string.IsNullOrWhiteSpace(accept) ||
                    accept.Contains("image", StringComparison.OrdinalIgnoreCase) ||
                    accept.Contains("jpeg", StringComparison.OrdinalIgnoreCase) ||
                    accept.Contains("jpg", StringComparison.OrdinalIgnoreCase) ||
                    accept.Contains("png", StringComparison.OrdinalIgnoreCase);
            }
            catch (StaleElementReferenceException) { return false; }
        });
        if (imageInput is not null) return imageInput;
        return inputs.Count == 1 ? inputs[0] : null;
    }

    private void ClickFacebookThumbnailDialogPoint(IWebDriver driver, int x, int y, string action)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                new OpenQA.Selenium.Interactions.Actions(driver)
                    .MoveToLocation(x, y)
                    .Click()
                    .Perform();
                Log($"Facebook viewport fixed thumbnail dialog {action} click attempt={attempt} x={x} y={y}");
                return;
            }
            catch (WebDriverException ex) when (attempt < 2)
            {
                Log($"Facebook thumbnail dialog {action} pointer retry: {ex.GetType().Name}");
                Thread.Sleep(700);
            }
        }

        throw new InvalidOperationException($"Facebook 缩略图窗口 {action} 点击失败: {x},{y}");
    }

    private void ClickFacebookFixedPoint(IWebDriver driver, int x, int y, string action)
    {
        var hit = (string?)((IJavaScriptExecutor)driver).ExecuteScript(
            "const raw = document.elementFromPoint(arguments[0], arguments[1]); " +
            "if (!raw) return 'none'; " +
            "const el = raw.closest('[role=button],button,a') || raw; " +
            "['pointerdown','mousedown','pointerup','mouseup','click'].forEach(t => " +
            "el.dispatchEvent(new MouseEvent(t,{bubbles:true,cancelable:true,view:window,clientX:arguments[0],clientY:arguments[1]}))); " +
            "return raw.tagName + '->' + el.tagName + ':' + (el.getAttribute('aria-label') || el.textContent || '');", x, y);
        Log($"Facebook fixed {action} click x={x} y={y} hit={Normalize(hit)}");
    }

    private void ClickFacebookThumbnailSave(IWebDriver driver)
    {
        // The dialog moves with the viewport, but the visible save label is stable.
        var result = (string?)((IJavaScriptExecutor)driver).ExecuteScript(
            "const labels=/储存|儲存|保存|Save/i; " +
            "const nodes=[...document.querySelectorAll('button,[role=button],input[type=button]')].filter(n=>{ " +
            "const r=n.getBoundingClientRect(), t=(n.getAttribute('aria-label')||n.textContent||n.value||'').trim(); " +
            "return r.width>30&&r.height>20&&labels.test(t)&&getComputedStyle(n).visibility!=='hidden'&& " +
            "getComputedStyle(n).display!=='none'&&r.bottom>0&&r.right>0; }); " +
            "const n=nodes[nodes.length-1]; if(n){n.click(); return 'dom:'+" +
            "(n.getAttribute('aria-label')||n.textContent||n.value||'')+'@'+n.getBoundingClientRect().left+','+n.getBoundingClientRect().top;} " +
            "return 'miss';");
        Log($"Facebook thumbnail save result={Normalize(result)}");
        if (result is not null && !result.Equals("miss", StringComparison.OrdinalIgnoreCase)) return;

        // Fallback uses the current viewport instead of a desktop-sized coordinate.
        var fallback = (string?)((IJavaScriptExecutor)driver).ExecuteScript(
            "const w=innerWidth,h=innerHeight; const x=Math.round(w*0.667), y=Math.round(h*0.79); " +
            "const raw=document.elementFromPoint(x,y); const el=raw?.closest('[role=button],button,input[type=button]'); " +
            "if(el){el.click(); return 'viewport:'+x+','+y+':'+(el.textContent||el.value||el.getAttribute('aria-label')||'');} return 'miss';");
        Log($"Facebook thumbnail save viewport fallback result={Normalize(fallback)}");
        if (fallback is null || fallback.Equals("miss", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Facebook 缩略图储存按钮未点击");
    }

    private void EnterFacebookReelTitle(IWebDriver driver, string title)
    {
        const int referenceX = 220;
        const int referenceY = 170;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var editor = new WebDriverWait(driver, TimeSpan.FromSeconds(10)).Until(d =>
                    d.FindElements(By.CssSelector("[contenteditable=true], textarea, [role=textbox]"))
                        .Where(x =>
                        {
                            try
                            {
                                if (!x.Displayed || !x.Enabled) return false;
                                var label = $"{x.GetAttribute("aria-label")} {x.GetAttribute("placeholder")} {x.GetAttribute("data-testid")}";
                                var location = x.Location;
                                var size = x.Size;
                                return size.Width > 100 && size.Height > 20 && location.X >= 80 && location.X < 360 &&
                                    location.Y >= 60 && location.Y < 360 &&
                                    (Regex.IsMatch(label, "介绍你的 Reel|介紹你的 Reel|description|caption|reel", RegexOptions.IgnoreCase) ||
                                     string.IsNullOrWhiteSpace(label));
                            }
                            catch (StaleElementReferenceException) { return false; }
                        })
                        .OrderBy(x => x.Location.Y)
                        .FirstOrDefault());
                if (editor is null) throw new InvalidOperationException("Facebook Reel 标题输入框未找到");

                Log($"Facebook scaled title click attempt={attempt} reference={referenceX},{referenceY} editor={editor.Location.X},{editor.Location.Y}");
                ClickFacebookScaledDomPoint(driver, referenceX, referenceY, "description field");
                PauseRandom(1200, 2200);
                editor.Click();
                editor.SendKeys(OpenQA.Selenium.Keys.Control + "a");
                foreach (var character in title)
                {
                    editor.SendKeys(character.ToString());
                    PauseRandom(60, 140);
                }
                PauseRandom(500, 900);
                var entered = (string?)((IJavaScriptExecutor)driver).ExecuteScript(
                    "const e=arguments[0]; return e ? (e.value || e.innerText || e.textContent || '') : '';", editor);
                Log($"Facebook title entered length={(entered ?? "").Length}");
                if (!string.IsNullOrEmpty(entered) && entered.Contains(title, StringComparison.Ordinal)) return;
                throw new InvalidOperationException($"Facebook Reel 标题输入后未读到内容，实际长度={(entered ?? "").Length}");
            }
            catch (WebDriverException ex) when (attempt < 3)
            {
                Log($"Facebook title entry transient error attempt={attempt}: {ex.GetType().Name}");
            }
            Thread.Sleep(500);
        }

        throw new InvalidOperationException("Facebook Reel 标题未成功填写");
    }

    private void PauseRandom(int minimumMilliseconds, int maximumMilliseconds)
    {
        Thread.Sleep(_random.Next(minimumMilliseconds, maximumMilliseconds + 1));
    }

    private void ClickFacebookThumbnailEdit(IWebDriver driver)
    {
        const int referenceX = 120;
        const int referenceY = 150;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                // The thumbnail overlay is a visual hit target and may not
                // expose the Edit control as a button. Click the calibrated
                // browser viewport coordinate directly.
                new OpenQA.Selenium.Interactions.Actions(driver)
                    .MoveToLocation(referenceX, referenceY)
                    .Click()
                    .Perform();
                Log($"Facebook viewport fixed thumbnail edit click attempt={attempt} " +
                    $"x={referenceX} y={referenceY}");
                return;
            }
            catch (WebDriverException ex) when (attempt < 2)
            {
                Log($"Facebook thumbnail edit pointer retry: {ex.GetType().Name}");
                Thread.Sleep(700);
            }
        }
        throw new InvalidOperationException("Facebook 缩略图编辑按钮固定坐标点击失败");
    }

    private static bool HasVisibleText(IWebDriver driver, params string[] labels)
        => FindFacebookActionButton(driver, labels) is not null ||
           driver.FindElements(By.XPath("//*[self::button or @role='button']"))
               .Any(x => x.Displayed && labels.Any(label =>
                   Normalize((x.Text ?? "")).Contains(Normalize(label), StringComparison.OrdinalIgnoreCase)));

    private static bool HasExactOrAccessibleLabel(IWebElement element, params string[] labels)
    {
        var values = new[] { element.Text, element.GetAttribute("aria-label"),
            element.GetAttribute("data-testid"), element.GetAttribute("title") }
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(Normalize).ToArray();
        return labels.Any(label => values.Any(value =>
            value.Equals(Normalize(label), StringComparison.OrdinalIgnoreCase)));
    }

    private static bool ContainsAccessibleLabel(IWebElement element, params string[] labels)
    {
        var values = new[] { element.Text, element.GetAttribute("aria-label"),
            element.GetAttribute("data-testid"), element.GetAttribute("title") }
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(Normalize).ToArray();
        return labels.Any(label => values.Any(value =>
            value.Contains(Normalize(label), StringComparison.OrdinalIgnoreCase)));
    }

    private static string DescribeControl(IWebElement? element)
        => element is null ? "<null>" : Normalize($"text={element.Text}; aria={element.GetAttribute("aria-label")}; title={element.GetAttribute("title")}");

    private static string VisibleControlSummary(IWebDriver driver)
        => string.Join(" | ", driver.FindElements(By.CssSelector("button, [role='button'], a"))
            .Where(x => x.Displayed && x.Enabled)
            .Select(DescribeControl)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Take(40));

    private static bool IsInVisibleMenu(IWebElement element)
    {
        try
        {
            return element.FindElements(By.XPath("ancestor-or-self::*[@role='menu' or @role='dialog' or @role='listbox']")).Any();
        }
        catch (StaleElementReferenceException) { return false; }
    }

    private static bool IsInsideHiddenOrDialog(IWebElement element)
    {
        try
        {
            return element.FindElements(By.XPath("ancestor::*[@aria-hidden='true']")).Any();
        }
        catch (StaleElementReferenceException) { return true; }
    }

    private static string Normalize(string? value)
        => Regex.Replace((value ?? "").Replace("\r", " ").Replace("\n", " ").Trim(), @"\s+", " ");

    private static string GetArtifactPath(object artifact)
        => artifact switch
        {
            string path => path,
            _ => artifact.GetType().GetProperty("artifact")?.GetValue(artifact)?.ToString()
                 ?? throw new InvalidOperationException("素材路径无效")
        };

    private void Stage(string name, object evidence)
    {
        Persist(("stage", name), ("stage_evidence", evidence), ("error_reason", null));
        Log($"stage={name} evidence={JsonSerializer.Serialize(evidence, _json)}");
    }

    private void Fail(string stage, string reason)
    {
        // A Facebook failure must not invalidate already verified local media.
        // That media is the checkpoint used to resume on the next run.
        var downloadStatus = ReadString("download_status");
        Persist(("stage", stage),
                ("workflow_status", "failed"),
                ("download_status", string.Equals(downloadStatus, "verified", StringComparison.OrdinalIgnoreCase)
                    ? "verified" : "blocked"),
                ("error_reason", reason));
        Log($"stage={stage} failed: {reason}");
    }

    private void Persist(params (string Key, object? Value)[] values)
    {
        foreach (var (key, value) in values)
            _state[key] = value;
        _state["last_checked_at"] = DateTime.UtcNow.ToString("O");
        var temp = _config.StateFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_state, _json));
        File.Move(temp, _config.StateFile, true);
    }

    private string? ReadString(string key) => _state.TryGetValue(key, out var value) ? value?.ToString() : null;

    private int ReadInt(string key)
        => int.TryParse(ReadString(key), out var value) ? Math.Max(0, value) : 0;

    private List<string> ReadStringList(string key)
    {
        if (!_state.TryGetValue(key, out var value)) return new();
        if (value is JsonElement element && element.ValueKind == JsonValueKind.Array)
            return element.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()!).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        return new();
    }

    private bool ReadBool(string key)
        => _state.TryGetValue(key, out var value) &&
           (value is bool b && b || string.Equals(value?.ToString(), "true", StringComparison.OrdinalIgnoreCase));

    private void Log(string message)
    {
        File.AppendAllText(_config.LogFile, $"{DateTime.Now:O} {message}{Environment.NewLine}");
        Console.WriteLine(message);
    }

    private static Dictionary<string, object?> LoadState(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) return new();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(File.ReadAllText(path))
                   ?? new();
        }
        catch (JsonException)
        {
            return new();
        }
    }
}

sealed class Config
{
    [JsonPropertyName("account")]
    public string Account { get; set; } = "";
    [JsonPropertyName("douyin_url")]
    public string DouyinUrl { get; set; } = "";
    [JsonPropertyName("adspower")]
    public AdsPowerConfig AdsPower { get; set; } = new();
    [JsonPropertyName("facebook_page")]
    public string FacebookPage { get; set; } = "";
    [JsonPropertyName("facebook_reel_url")]
    public string FacebookReelUrl { get; set; } = "";
    [JsonPropertyName("state_file")]
    public string StateFile { get; set; } = "";
    [JsonPropertyName("log_file")]
    public string LogFile { get; set; } = "";
    [JsonPropertyName("artifact_dir")]
    public string ArtifactDir { get; set; } = "";
    [JsonPropertyName("chrome_user_data_dir")]
    public string? ChromeUserDataDir { get; set; } = "chrome-profile";
    [JsonPropertyName("chrome_profile_directory")]
    public string? ChromeProfileDirectory { get; set; } = "Default";
    // Keep old config files readable after the local browser migration.
    [JsonPropertyName("edge_user_data_dir")]
    public string? LegacyEdgeUserDataDir { get; set; }
    [JsonPropertyName("edge_profile_directory")]
    public string? LegacyEdgeProfileDirectory { get; set; }
    [JsonPropertyName("manual_login_required")]
    public bool ManualLoginRequired { get; set; } = true;
    [JsonPropertyName("poll_interval_seconds")]
    public int PollIntervalSeconds { get; set; } = 1800;
    [JsonPropertyName("dry_run")]
    public bool DryRun { get; set; }
    [JsonPropertyName("auto_publish")]
    public bool AutoPublish { get; set; }
    [JsonPropertyName("vision")]
    public VisionConfig? Vision { get; set; }
    [JsonPropertyName("accounts")]
    public List<AccountConfig> Accounts { get; set; } = new();
    [JsonPropertyName("accounts_file")]
    public string? AccountsFile { get; set; }
    [JsonPropertyName("schedule")]
    public ScheduleConfig Schedule { get; set; } = new();

    public List<Config> ExpandAccounts()
    {
        var configuredAccounts = Accounts;
        if (!string.IsNullOrWhiteSpace(AccountsFile))
        {
            var csvPath = Path.IsPathRooted(AccountsFile)
                ? AccountsFile
                : Path.Combine(AppContext.BaseDirectory, AccountsFile);
            if (File.Exists(csvPath))
                configuredAccounts = LoadAccountsCsv(csvPath);
        }
        if (configuredAccounts.Count == 0)
            return new() { this };

        var enabledAccounts = configuredAccounts.Where(account => account.Enabled).ToList();
        if (enabledAccounts.Count == 0)
            throw new InvalidOperationException("accounts 中没有启用的账号");
        if (enabledAccounts.GroupBy(x => x.AdsPower.UserId, StringComparer.OrdinalIgnoreCase).Any(x => string.IsNullOrWhiteSpace(x.Key) || x.Count() > 1))
            throw new InvalidOperationException("每个账号必须填写不同的 AdsPower user_id");
        return enabledAccounts.Select(account => new Config
        {
            Account = account.Account,
            DouyinUrl = account.DouyinUrl,
            AdsPower = account.AdsPower,
            FacebookPage = account.FacebookPage,
            FacebookReelUrl = account.FacebookReelUrl,
            StateFile = account.StateFile ?? $"data\\accounts\\{account.Account}\\_state.json",
            LogFile = account.LogFile ?? $"data\\accounts\\{account.Account}\\worker.log",
            ArtifactDir = account.ArtifactDir ?? $"data\\accounts\\{account.Account}",
            ChromeUserDataDir = account.ChromeUserDataDir ?? "chrome-profile",
            ChromeProfileDirectory = account.ChromeProfileDirectory ?? "Default",
            LegacyEdgeUserDataDir = account.LegacyEdgeUserDataDir,
            LegacyEdgeProfileDirectory = account.LegacyEdgeProfileDirectory,
            ManualLoginRequired = account.ManualLoginRequired ?? ManualLoginRequired,
            PollIntervalSeconds = PollIntervalSeconds,
            DryRun = DryRun,
            AutoPublish = account.AutoPublish ?? AutoPublish,
            Vision = account.Vision ?? Vision
        }).ToList();
    }

    private List<AccountConfig> LoadAccountsCsv(string path)
    {
        var rows = ReadCsvText(path)
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(ParseCsvLine)
            .ToList();
        if (rows.Count <= 1) return new();

        return rows.Skip(1)
            .Where(row => row.Count >= 4 &&
                          row.Any(value => !string.IsNullOrWhiteSpace(value)) &&
                          !string.IsNullOrWhiteSpace(row[1]) &&
                          !string.IsNullOrWhiteSpace(row[2]) &&
                          !string.IsNullOrWhiteSpace(row[3]))
            .Select(row => new AccountConfig
        {
            Enabled = row.Count < 6 || !string.Equals(row[5].Trim(), "false", StringComparison.OrdinalIgnoreCase),
            AutoPublish = row.Count > 6 ? ParseBoolean(row[6]) : null,
            AdsPower = new AdsPowerConfig { ProfileName = row[0].Trim(), UserId = row[1].Trim() },
            Account = row[2].Trim(),
            DouyinUrl = row[3].Trim(),
            FacebookPage = row.Count > 4 && !string.IsNullOrWhiteSpace(row[4]) ? row[4].Trim() : FacebookPage
        }).ToList();
    }

    private static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"' && quoted && i + 1 < line.Length && line[i + 1] == '"') { field.Append('"'); i++; }
            else if (c == '"') quoted = !quoted;
            else if (c == ',' && !quoted) { result.Add(field.ToString()); field.Clear(); }
            else field.Append(c);
        }
        result.Add(field.ToString());
        return result;
    }

    private static bool? ParseBoolean(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return bool.TryParse(value.Trim(), out var result) ? result : null;
    }

    private static string ReadCsvText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(936).GetString(bytes);
        }
    }

}

sealed class AccountConfig
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("account")] public string Account { get; set; } = "";
    [JsonPropertyName("douyin_url")] public string DouyinUrl { get; set; } = "";
    [JsonPropertyName("adspower")] public AdsPowerConfig AdsPower { get; set; } = new();
    [JsonPropertyName("facebook_page")] public string FacebookPage { get; set; } = "";
    [JsonPropertyName("facebook_reel_url")] public string FacebookReelUrl { get; set; } = "";
    [JsonPropertyName("state_file")] public string? StateFile { get; set; }
    [JsonPropertyName("log_file")] public string? LogFile { get; set; }
    [JsonPropertyName("artifact_dir")] public string? ArtifactDir { get; set; }
    [JsonPropertyName("chrome_user_data_dir")] public string? ChromeUserDataDir { get; set; }
    [JsonPropertyName("chrome_profile_directory")] public string? ChromeProfileDirectory { get; set; }
    [JsonPropertyName("edge_user_data_dir")] public string? LegacyEdgeUserDataDir { get; set; }
    [JsonPropertyName("edge_profile_directory")] public string? LegacyEdgeProfileDirectory { get; set; }
    [JsonPropertyName("manual_login_required")] public bool? ManualLoginRequired { get; set; }
    [JsonPropertyName("auto_publish")] public bool? AutoPublish { get; set; }
    [JsonPropertyName("vision")] public VisionConfig? Vision { get; set; }
}

sealed class ScheduleConfig
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("start_time")] public string StartTime { get; set; } = "12:00";
    [JsonPropertyName("end_time")] public string EndTime { get; set; } = "23:00";
    [JsonPropertyName("interval_minutes")] public int IntervalMinutes { get; set; } = 30;

    public bool IsWithinWindow(DateTime now)
    {
        if (!Enabled) return true;
        if (!TimeSpan.TryParse(StartTime, out var start) || !TimeSpan.TryParse(EndTime, out var end))
            throw new InvalidOperationException("schedule.start_time/end_time 必须是 HH:mm");
        var current = now.TimeOfDay;
        return start <= end ? current >= start && current <= end : current >= start || current <= end;
    }

    public int SecondsUntilNextCheck(DateTime now)
    {
        var interval = Math.Clamp(IntervalMinutes, 1, 1440) * 60;
        if (!Enabled || IsWithinWindow(now)) return interval;
        if (!TimeSpan.TryParse(StartTime, out var start)) return interval;
        var next = now.Date.Add(start);
        if (next <= now) next = next.AddDays(1);
        return Math.Max(1, (int)Math.Min(int.MaxValue, (next - now).TotalSeconds));
    }
}

sealed class MonitorState
{
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, MonitorAccount> _accounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _start;
    private readonly string _end;
    private readonly int _interval;
    private DateTime? _next;
    private string _overall = "启动中";
    private string? _systemError;

    public MonitorState(string start, string end, int interval)
    {
        _start = start;
        _end = end;
        _interval = Math.Clamp(interval, 1, 1440);
    }

    public void SetAccount(string name, string browser, string status, string? error, string? stateFile)
    {
        var transferStatus = ReadTransferStatus(stateFile);
        var successTime = ReadSuccessTime(stateFile);
        _accounts.AddOrUpdate(name,
            _ => new MonitorAccount(name, browser, transferStatus, successTime, status, DateTime.Now, error),
            (_, old) => old with { Browser = browser, Status = status, TransferStatus = transferStatus, SuccessTime = successTime, CheckedAt = DateTime.Now, Error = error });
    }

    private static string SuccessTimeText(DateTime value)
        => value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    private static string? ReadSuccessTime(string? stateFile)
    {
        if (string.IsNullOrWhiteSpace(stateFile) || !File.Exists(stateFile)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(stateFile));
            if (!document.RootElement.TryGetProperty("success_time", out var value)) return null;
            if (!DateTime.TryParse(value.GetString(), out var parsed)) return null;
            return SuccessTimeText(parsed);
        }
        catch { return null; }
    }

    private static string ReadTransferStatus(string? stateFile)
    {
        if (string.IsNullOrWhiteSpace(stateFile) || !File.Exists(stateFile)) return "未更新未搬运";
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(stateFile));
            var root = document.RootElement;
            var completed = root.TryGetProperty("last_successfully_processed_video_id", out var completedValue)
                ? completedValue.GetString() : null;
            var latest = root.TryGetProperty("stage_evidence", out var evidence) && evidence.ValueKind == JsonValueKind.Object && evidence.TryGetProperty("VideoId", out var video)
                ? video.GetString() : null;
            latest ??= root.TryGetProperty("active_video_id", out var active) ? active.GetString() : null;
            if (string.IsNullOrWhiteSpace(latest)) return "未更新未搬运";
            return string.Equals(latest, completed, StringComparison.OrdinalIgnoreCase)
                ? "已更新已搬运" : "已更新未搬运";
        }
        catch { return "未更新未搬运"; }
    }

    public void SetNext(DateTime next)
    {
        lock (_gate) _next = next;
    }

    public void SetOverall(string value)
    {
        lock (_gate) _overall = value;
    }

    public void SetSystemError(string? error)
    {
        lock (_gate) _systemError = error;
    }

    public MonitorSnapshot ReadSnapshot()
    {
        DateTime? next;
        string overall;
        lock (_gate) { next = _next; overall = _overall; }
        string? systemError;
        lock (_gate) systemError = _systemError;
        return new MonitorSnapshot(overall, _start, _end, _interval, next, systemError,
            _accounts.Values.OrderBy(x => x.Name).ToList());
    }

    public object Snapshot()
    {
        DateTime? next;
        lock (_gate) next = _next;
        return new
        {
            running = true,
            start = _start,
            end = _end,
            interval = _interval,
            next = next?.ToString("yyyy-MM-dd HH:mm:ss"),
            accounts = _accounts.Values.OrderBy(x => x.Name).Select(x => new
            {
                name = x.Name,
                browser = x.Browser,
                status = x.Status,
                checkedAt = x.CheckedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                video = "",
                error = x.Error
            })
        };
    }
}

record MonitorAccount(string Name, string Browser, string TransferStatus, string? SuccessTime, string Status, DateTime CheckedAt, string? Error);
record MonitorSnapshot(string Overall, string Start, string End, int Interval, DateTime? Next, string? SystemError, List<MonitorAccount> Accounts);

sealed class MonitorForm : Form
{
    private readonly MonitorState _state;
    private readonly ScheduleConfig _schedule;
    private readonly Label _status = new();
    private readonly Label _next = new();
    private readonly DataGridView _grid = new();
    private readonly NotifyIcon _tray = new();
    private readonly EventWaitHandle _showExistingInstance;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 5000 };
    private bool _allowClose;

    public MonitorForm(MonitorState state, ScheduleConfig schedule, EventWaitHandle showExistingInstance)
    {
        _state = state;
        _schedule = schedule;
        _showExistingInstance = showExistingInstance;
        Text = "抖音账号监控";
        Icon = LoadAppIcon();
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        MinimizeBox = true;
        ClientSize = new Size(980, 560);
        MinimumSize = new Size(720, 420);

        BackColor = Color.FromArgb(243, 246, 249);
        var header = new Panel { Dock = DockStyle.Top, Height = 112, BackColor = Color.FromArgb(19, 35, 47), Padding = new Padding(24, 12, 24, 10) };
        var title = new Label { Text = "搬运助手", ForeColor = Color.White, Font = new Font("Microsoft YaHei", 18, FontStyle.Bold), Location = new Point(24, 12), AutoSize = true };
        var subtitle = new Label { Text = "抖音监控与 Facebook 发布中心", ForeColor = Color.FromArgb(157, 185, 198), Font = new Font("Microsoft YaHei", 9), Location = new Point(26, 48), AutoSize = true };
        _status.SetBounds(26, 53, 430, 24);
        _status.SetBounds(26, 76, 430, 24);
        _status.ForeColor = Color.FromArgb(196, 218, 226);
        _status.Font = new Font("Microsoft YaHei", 10);
        _next.SetBounds(500, 76, 440, 24);
        _next.ForeColor = Color.FromArgb(196, 218, 226);
        _next.Font = new Font("Microsoft YaHei", 10);
        header.Controls.AddRange([title, subtitle, _status, _next]);

        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.RowHeadersVisible = false;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        _grid.ColumnHeadersHeight = 36;
        _grid.RowTemplate.Height = 34;
        _grid.BackgroundColor = Color.White;
        _grid.BorderStyle = BorderStyle.FixedSingle;
        _grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        _grid.GridColor = Color.FromArgb(231, 236, 240);
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.FromArgb(232, 238, 242),
            ForeColor = Color.FromArgb(43, 57, 65),
            Font = new Font("Microsoft YaHei", 9, FontStyle.Bold),
            SelectionBackColor = Color.FromArgb(232, 238, 242),
            SelectionForeColor = Color.FromArgb(43, 57, 65),
            Padding = new Padding(8, 6, 8, 6)
        };
        _grid.DefaultCellStyle = new DataGridViewCellStyle
        {
            Font = new Font("Microsoft YaHei", 9),
            ForeColor = Color.FromArgb(49, 63, 71),
            SelectionBackColor = Color.FromArgb(220, 240, 244),
            SelectionForeColor = Color.FromArgb(24, 52, 60),
            Padding = new Padding(8, 5, 8, 5)
        };
        _grid.Columns.Add("name", "抖音账号");
        _grid.Columns.Add("browser", "浏览器");
        _grid.Columns.Add("transfer", "搬运状态");
        _grid.Columns.Add("success", "成功时间");
        _grid.Columns.Add("status", "状态");
        _grid.Columns.Add("checked", "最近检查");
        _grid.Columns.Add("error", "错误");
        _grid.Columns["name"]!.FillWeight = 24;
        _grid.Columns["browser"]!.FillWeight = 24;
        _grid.Columns["transfer"]!.FillWeight = 24;
        _grid.Columns["success"]!.FillWeight = 25;
        _grid.Columns["status"]!.FillWeight = 18;
        _grid.Columns["checked"]!.FillWeight = 25;
        _grid.Columns["error"]!.FillWeight = 30;
        _grid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || _grid.Columns[e.ColumnIndex].Name is not ("status" or "transfer")) return;
            var value = e.Value?.ToString() ?? "";
            e.CellStyle.ForeColor = value.Contains("未") || value.Contains("失败") ? Color.FromArgb(190, 55, 55) :
                value.Contains("执行中") ? Color.FromArgb(0, 126, 137) : Color.FromArgb(54, 119, 78);
            e.CellStyle.Font = new Font("Microsoft YaHei", 9, FontStyle.Bold);
        };

        Controls.Add(_grid);
        Controls.Add(header);
        _tray.Icon = Icon ?? SystemIcons.Application;
        _tray.Text = "抖音账号监控";
        _tray.Visible = true;
        var menu = new ContextMenuStrip();
        menu.Items.Add("显示监控界面", null, (_, _) => ShowFromTray());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出程序", null, (_, _) => ExitFromTray());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowFromTray();
        FormClosing += OnClosing;
        Shown += (_, _) =>
        {
            _tray.Visible = true;
            WindowState = FormWindowState.Normal;
            Activate();
        };
        _timer.Tick += (_, _) =>
        {
            if (_showExistingInstance.WaitOne(0)) ShowFromTray();
            RefreshView();
        };
        _timer.Start();
        RefreshView();
    }

    private static Icon LoadAppIcon()
    {
        try { return new Icon(Path.Combine(AppContext.BaseDirectory, "qingyan.ico")); }
        catch
        {
            try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application; }
            catch { return SystemIcons.Application; }
        }
    }

    private void ShowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    private void HideToTray()
    {
        _tray.Visible = true;
        Hide();
        _tray.BalloonTipTitle = "抖音账号监控";
        _tray.BalloonTipText = "程序仍在后台监控，可从系统托盘恢复界面。";
        _tray.ShowBalloonTip(1200);
    }

    private void ExitFromTray()
    {
        _allowClose = true;
        _tray.Visible = false;
        Close();
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_allowClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideToTray();
        }
    }

    private void RefreshView()
    {
        var snapshot = _state.ReadSnapshot();
        _status.Text = $"状态：{snapshot.Overall}    时间段：{snapshot.Start}-{snapshot.End}    间隔：{snapshot.Interval} 分钟";
        _next.Text = $"下一次检查：{snapshot.Next?.ToString("yyyy-MM-dd HH:mm:ss") ?? "计算中"}";
        if (!string.IsNullOrWhiteSpace(snapshot.SystemError))
            _next.Text += $"    错误：{snapshot.SystemError}";
        _grid.Rows.Clear();
        foreach (var account in snapshot.Accounts)
            _grid.Rows.Add(account.Name, account.Browser, account.TransferStatus, account.SuccessTime ?? "--", account.Status, account.CheckedAt.ToString("yyyy-MM-dd HH:mm:ss"), account.Error ?? "");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Stop();
            _timer.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
        }
        base.Dispose(disposing);
    }
}

sealed class VisionConfig
{
    [JsonPropertyName("api_key")]
    public string ApiKey { get; set; } = "";
    [JsonPropertyName("endpoint")]
    public string Endpoint { get; set; } = "https://blankapi.com/v1/chat/completions";
    [JsonPropertyName("model")]
    public string Model { get; set; } = "gpt-5.6-terra";
    [JsonPropertyName("api_key_environment")]
    public string ApiKeyEnvironment { get; set; } = "BLANKAPI_API_KEY";
    [JsonPropertyName("timeout_seconds")]
    public int TimeoutSeconds { get; set; } = 20;
}

sealed class AdsPowerConfig
{
    [JsonPropertyName("profile_name")]
    public string ProfileName { get; set; } = "";
    [JsonPropertyName("user_id")]
    public string UserId { get; set; } = "";
    [JsonPropertyName("api_base")]
    public string ApiBase { get; set; } = "http://127.0.0.1:50325";
}

record ScanResult(
    string Url,
    string Account,
    bool AccountFound,
    int WorksCount,
    List<string> PinnedVideoIds,
    string VideoId,
    string Title);

record ExtractionResult(
    string Title,
    string OriginalUrl,
    string CoverPath,
    string DownloadDir);

record ArtifactResult(
    object Video,
    object Cover,
    object Title);

record AdsPowerHealth(
    bool Ok,
    string? Reason,
    string? DebugPort,
    string? WebDriver);

record FacebookUploadResult(
    object Editor,
    object Media,
    object Cover,
    bool Published);

static class NativeMethods
{
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FreeConsole();
}
