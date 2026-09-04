# 情焱搬运助手

本地 Windows 任务引擎，用于运行“叶禾说心理”搬运任务。

当前版本提供：

- JSON 配置
- 多账号顺序轮询：每个抖音账号绑定自己的 AdsPower 浏览器和 Facebook 页面
- 时间窗口和轮询间隔配置
- 阶段状态机和断点恢复
- 每个账号独立记录已下载视频 ID，避免重复下载
- 单阶段有限重试
- AdsPower 健康检查
- 运行日志
- 模拟执行模式，便于先验证调度和状态流程

外部平台仍需要网络和登录状态。Facebook 最终发布动作不会由程序自动执行。

## 运行

```powershell
python .\worker.py --config .\config.json --once --dry-run
```

发布后的原生程序运行方式：

```powershell
.\publish\QingyanMover.exe --config .\config.json --once --dry-run
```

安装 .NET 8 SDK 后重新发布：

```powershell
dotnet publish .\QingyanMover.csproj -c Release -o .\publish
```

真实运行前，先确认配置中的抖音主页、AdsPower `user_id` 和输出目录。

## 多账号和定时查询

配置文件入口是 `publish\\config.json`。在 `accounts` 数组中，每个对象填写一套对应关系：

程序唯一配置文件和账号表都在 `publish` 目录：`publish\\config.json`、`publish\\accounts.csv`。CSV 可以用 Excel 打开，增加、修改或删除一行即可调整账号队列。CSV 列顺序为：`浏览器名称`、`浏览器ID`、`抖音账号名称`、`抖音账号主页链接`、`Facebook页面`、`启用`。所有账号固定共用 `publish\\chrome-profile`，只需登录一次抖音。

- `account`：抖音账号显示名称
- `enabled`：是否参与轮询，临时停用填 `false`
- `douyin_url`：该账号的抖音主页链接
- `adspower.user_id`：对应的 AdsPower 浏览器 ID
- `adspower.profile_name`：浏览器备注，仅用于核对
- `facebook_page`：该浏览器中要上传的 Facebook 页面

程序会按 `accounts` 数组顺序逐个查询。每个账号的状态、日志和下载文件默认分别写入 `data\\accounts\\账号名`，互不混用。

定时设置在 `schedule`：

```json
"schedule": {
  "enabled": true,
  "start_time": "12:00",
  "end_time": "23:00",
  "interval_minutes": 30
}
```

这表示每天 12:00 到 23:00 之间执行，每轮完成所有账号后等待 30 分钟。使用 `--once` 手动测试时会立即执行一轮，不受时间窗口限制。

## 状态

状态默认写入：

`C:\Users\Administrator\Documents\Codex\douyin_tasks\叶禾说心理\_state.json`

日志默认写入：

`C:\Users\Administrator\Documents\Codex\douyin_tasks\叶禾说心理\worker.log`
