# Aurora 开发指南（DEVELOPMENT）

## 环境

- Windows 10 1809+ / Windows 11；.NET 10 SDK（本机：`C:\Users\Jinwei\AppData\Local\Microsoft\dotnet`）
- 无额外依赖：NuGet 锁定版本还原（NAudio/NVorbis/Microsoft.Data.Sqlite），Concentus 随 `lib/` 本地 DLL

## 构建与测试

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1      # 4 步：主程序 → 卸载器 → 安装器 → Portable
dotnet test tests/Aurora.Tests.csproj                    # 全量测试（无声卡，CI 可跑）
```

- 产物：`build/`（主程序）、根目录 `AuroraPlayer-Setup.exe`（约 30MB，内嵌 WinRT 投影）、`Aurora-x64-Portable.exe`（自包含）
- 测试进程若新扩展 GBK 相关用例，需显式 `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)`

## 分支模型

- `main`：可发布状态，随时可打 tag
- `develop`：集成分支，各阶段改动在此进行，稳定后合回 main
- 发布：打 `v*` tag → CI 自动 Build+Test+Release（三产物）
- 当前基线：tag `v2.0-stable`（2026-09-09 五项改进后）

## 代码组织约定

- `src/` 扁平单工程（不建子目录）；**csproj `EnableDefaultCompileItems=false`，新增 .cs 必须手工加入 AuroraPlayer.csproj `<Compile Include>`**
- MVVM 四层 + View-Controller 模式：控制器构造注入 win/控件/VM/回调，自挂事件，由 MainWindow.FindControls 创建；详见 docs/ARCHITECTURE.md 分层图
- 中文注释；文件头 `/* ==== 职责说明 ==== */` 风格
- UI 线程切换**只用 Dispatcher.BeginInvoke**（`SynchronizationContext` 在 app.Run() 前为 null，禁用）
- 单一数据源：列表唯一源 = PlaylistManager；播放业务唯一入口 = MainViewModel.PlayTrack；写入一律经 ViewModel

## 测试约定

- 纯逻辑类直测；不碰音频设备/WPF 控件；引擎测试走 `IAudioOutput` fake 注入（见 docs/AUDIO_PIPELINE.md）
- xUnit：测试类**只允许一个公共构造**（无参）
- 临时文件/目录在 Dispose 清理；测试间不共享状态

## 提交规范

`<type>: 中文摘要`（type：feat / fix / refactor / docs / test / perf / build / ci）；
跨阶段大改动在正文列分项说明。每次功能性提交保持 191+ 测试全绿。

## 日志与诊断

- `%LOCALAPPDATA%\Aurora\Logs\aurora.log`（>5MB 轮转）+ `crash_*.log`（三路钩子：Dispatcher/AppDomain/Main）
- 分级日志/诊断面板为 3.0 计划项（docs/ROADMAP.md 阶段 7/8）

## 发布前检查

1. `build.ps1` 全绿
2. `dotnet test` 全绿
3. 真机冒烟：播放/暂停/切歌/跨淡/键盘/拖放/联网匹配/换目录
4. 更新 `AppInfo.Version` → 提交 → 打 `v{Version}` tag 推送
