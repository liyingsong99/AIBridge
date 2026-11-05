# 原生 Player 构建

`build` 直接使用 Unity BuildPipeline，不依赖官方 CLI、Pipeline 或 Memory Profiler。最小命令面为 `run/status/report/release`。

```powershell
$CLI = './.aibridge/cli/AIBridgeCLI.exe'
& $CLI build run --scenes '["Assets/Scenes/SampleScene.unity"]'
& $CLI build run --scenes '["Assets/Scenes/SampleScene.unity"]' --dry-run false --confirm true
& $CLI build status --requestId <original-requestId>
& $CLI build report --requestId <original-requestId>
```

`run` 默认只验证。实际构建须逐次 `--dry-run false --confirm true`；仅当前 active BuildTarget，不自动切目标或安装模块。场景为显式 JSON 字符串数组，省略时取已启用的 BuildSettings 场景；必须为 1–128 个保存的 Assets SceneAsset。不保存当前打开场景、不改 BuildSettings 场景列表。选项仅 `--development true|false`；不开放自定义输出、任意 BuildOptions 或 AutoRun。

当前有界目标为 Windows/Windows64、macOS、Linux64、Android APK、iOS、WebGL。Android app bundle/export 配置暂不支持。模块与 AIBridge Runtime 构建宏须已同步；API 先只读检查，不在构建中同步宏。不主动改 PlayerSettings，用户或第三方构建回调仍由 Unity 正常执行。

## 回执与状态

构建意图先原子落盘，返回 `queued` 和原始 requestId。下一 Editor update 在主线程同步执行 `BuildPipeline.BuildPlayer`。排队回执不代表开始或成功；CLI 等待超时不停止构建，禁止重发原请求。Build 与 AIBridge code 执行互斥。

CLI `build`（默认 status）、`build status/report` 直接读 `.aibridge/build-operations/` 的纯 DTO，不排入 Editor 命令队列；因此构建阻塞主线程时仍可查询。`localJournal=true` 表示最后持久观察，不是实时进程存活保证。`status` 的外层 success 表示查询成功；真实结果见 `record.success`，未知为显式 null。`report` 仅 confirmed Succeeded 返回成功退出码；失败报告保留数据但返回失败。缺报告、损坏记录、多个 unresolved 记录均不能成功假装 idle。

状态为 queued/building/completed/unknown。完成报告保留 Unity result、错误/警告总数、耗时、大小、文件总数、步骤及消息；最多保留 256 文件/128 步/每步32消息/256Ki 字符正文，截断标记 `truncated=true`，总计数保留。Succeeded 且 totalErrors=0 才可成功。不把异常/缺报告当成功，也不序列化 Unity BuildReport 对象。

## 产物与恢复

每次独有 `.aibridge/builds/<random-buildId>/`，拒绝已存在目录和祖先重解析点，不覆盖已有产物。操作日志按 requestId SHA-256 命名，先发布单 ID 再发布 latest；单 ID 是权威状态，latest 失败不撤销已发布记录。默认查询会扫描登记记录，不能因 latest 丢失忘记忙作业。

reload/restart 不重建构建计划、不自动重放；旧 queued/building 恢复 unknown 并继续占位。显式查询原 ID，确认审查后可 `build release --requestId <id> --confirm true` 仅释放本地 unknown；不会取消同步构建，也不能证明未知构建未产生副作用。执行中的 BuildPlayer 不能 release。多个旧世代记录可逐个原 ID 释放，坏日志须人工核实，不自动删除。

## 验证记录与限制

2026-11-05，Unity 6000.0.51f1：定向75项，74通过/0失败/1跳过（宿主无符号链接创建 API）；Unity 编译、CLI测试通过。真实请求 `cmd_1793854259892_62c8e43e60c248` 已验证 queued→building（CLI主线程外可读）→completed + Failed/false/1error，332 文件保留256并标截断。本机缺少 IL2CPP C++ 编译工具链与 Windows SDK，真实 Player 成功输出尚未验证，未安装工具或改脚本后端。成功 DTO/单次调用、异常 unknown、IO失败、reload不重放、release、索引丢失/冲突均由假构建器/严格Store fixture覆盖。

2019.4/6000.6 编译、其他平台实际构建与成功产物启动仍需独立验证，不能把当前测试当跨版本发布保证。开发底稿见项目 `.aibridge/plan/pipeline-build-implementation.md`。
