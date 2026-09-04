# Windows 安装包

在项目根目录执行：

```powershell
powershell -ExecutionPolicy Bypass -File .\installer\build-installer.ps1
```

脚本会发布自包含的 Windows x64 程序，并生成 `installer\dist`：

- 已安装 Inno Setup 6：生成 `QingyanMover-Setup-1.0.0.exe`
- 未安装 Inno Setup 6：生成可直接解压运行的 `QingyanMover-portable.zip`

安装包不包含真实账号、浏览器登录目录、下载素材、日志或 API Key。首次运行前请编辑安装目录中的 `config.json` 和 `accounts.csv`，并安装、登录 AdsPower。
