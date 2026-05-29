# Code Index Snapshot Design

## 背景

当前 `code_index` 使用 `MSBuildWorkspace` 加载 Unity 生成的 `.sln/.csproj`。这种方式会把语义索引能力绑定到用户机器上的 MSBuild、.NET SDK、Visual Studio Build Tools 或相关 resolver。测试中已经出现 SDK 版本不匹配导致 daemon 无法加载 solution 的问题。

AIBridge 需要兼容大量 Unity 2019.4.x 项目。Unity 2019.4 的 C#、程序集生成、工程文件结构和较新 Unity 版本差异明显，因此长期维护 MSBuild / SDK 兼容矩阵会增加复杂度，并降低稳定性。

本方案将 `code_index` 的主架构改为 Unity 编译快照，不再依赖 MSBuild。

## 目标

1. 移除 `MSBuildWorkspace`、`Microsoft.Build.*`、`Microsoft.Build.Locator` 和 BuildHost 依赖。
2. 不要求用户安装 .NET SDK、Visual Studio、Build Tools、Rider 或 IDE 插件。
3. 兼容 Unity 2019.4.x；Unity Editor 侧代码按 C# 7.3 兼容写法实现。
4. 大工程可增量更新：某些程序集未变化时，不重写快照、不重建索引。
5. 查询按需加载，避免 `warmup` 阶段全量构建 Roslyn Compilation。

## 总体架构

```text
Unity Editor
  -> 通过 CompilationPipeline 生成 Unity 编译快照
  -> 写入 .aibridge/code-index/snapshot/

CodeIndex Daemon
  -> 读取快照 manifest
  -> 按需读取程序集快照
  -> 用 Roslyn AdhocWorkspace 构建 Project graph
  -> 执行 symbol / definition / references / callers / diagnostics 查询
```

Roslyn 仍作为语义分析库保留，但只使用 `AdhocWorkspace` 和 C# workspace 能力，不再使用 MSBuild 加载工程。

## 文件布局

```text
.aibridge/code-index/
  config.json
  status.json
  lock.json

  snapshot/
    manifest.bin
    manifest.json

    assemblies/
      Assembly-CSharp.bin
      Assembly-CSharp-Editor.bin
      cn.lys.aibridge.Editor.bin
      cn.lys.aibridge.Runtime.bin

  index/
    names/
      Assembly-CSharp.idx
      cn.lys.aibridge.Editor.idx

    tokens/
      Assembly-CSharp.idx
      cn.lys.aibridge.Editor.idx
```

`snapshot/manifest.bin` 是 daemon 的主入口，`manifest.json` 只用于排查和人工阅读。每个程序集单独一个 `assemblies/*.bin`，每个程序集也有独立索引文件。

## 快照生成

Editor 侧使用 `UnityEditor.Compilation.CompilationPipeline.GetAssemblies()` 采集 Unity 实际编译图。每个程序集记录：

- assembly name
- assembly output path
- source files
- define symbols
- compiled assembly references
- project assembly references
- asmdef path
- build target
- Unity version
- language version
- unsafe / compiler options
- source file size / timestamp / hash
- reference file size / timestamp / hash

Unity 2019.4 和高版本 Unity 的 CompilationPipeline API 可能存在字段差异，采集逻辑需要使用反射兜底，避免直接依赖新版本 API。

## 分片快照

快照按 Unity 程序集拆分，而不是写成一个大文件。

全局 manifest 记录程序集图和摘要信息：

```text
schemaVersion
projectRootHash
unityVersion
buildTarget
createdAt
generationId
assemblyRecords[]
```

每个 assembly record 记录：

```text
assemblyName
assemblyId
snapshotFile
nameIndexFile
tokenIndexFile
sourceFileCount
referenceCount
definesHash
sourcesHash
referencesHash
compilerOptionsHash
assemblyHash
dependencyAssemblyIds[]
reverseDependencyAssemblyIds[]
lastWriteTime
```

`assemblyHash` 由源码列表、源码状态、defines、references、compiler options、asmdef 内容、build target 和 Unity version 共同计算。hash 不变时，该程序集快照和索引都可以复用。

## 二进制格式

主格式使用自定义二进制格式，避免大工程 JSON 解析成本和重复字符串膨胀。实现上使用 `BinaryWriter` / `BinaryReader`，不引入 MessagePack 等第三方依赖。

建议基本结构：

```text
Header:
  magic = AIBCI
  schemaVersion
  formatKind
  createdAt

StringTable:
  去重字符串表

PathTable:
  root-relative 路径，统一使用 /

Payload:
  manifest records 或 assembly records
```

路径必须规范化为 project-root-relative，避免不同机器或不同 checkout 路径导致快照不可比较。

## 增量更新

Editor 生成快照时执行以下流程：

1. 采集当前 Unity 编译图。
2. 计算每个程序集的新 `assemblyHash`。
3. hash 未变化的程序集不重写 `assemblies/*.bin`，不重建 name/token index。
4. hash 变化的程序集先写临时文件，再原子替换。
5. 所有程序集处理完成后，最后写入新的 manifest。

daemon 读取 manifest 时通过 `generationId` 和 assembly hash 判断哪些程序集需要失效。正在查询时遇到 manifest 更新，应完成当前查询，再在下一次查询前刷新受影响程序集。

## 依赖失效

程序集自身没变，但它依赖的程序集 public API 变化时，它的语义结果可能变化。第一版采用保守策略：

- 自身 `assemblyHash` 变化：重建自身快照和索引。
- 自身依赖列表变化：重建自身快照和索引。
- 被依赖程序集变化：反向依赖程序集标记为 `dependencyStale`。
- `dependencyStale` 程序集可以继续服务轻量 `symbol` 查询，但 `references`、`callers`、`diagnostics` 等语义查询需要按需重建 Roslyn Project/Compilation。

后续可以引入 public API fingerprint，只在公开 API 指纹变化时影响下游程序集。

## 查询策略

`warmup` 不做全量语义构建，只做：

1. 读取 manifest。
2. 校验快照版本。
3. 构建程序集依赖图。
4. 校验关键引用是否存在。
5. 返回 `semantic=true`。

具体查询按需加载：

- `symbol`：优先读 name index，不构建全工程 Compilation。
- `definition`：加载目标文件所属程序集。
- `references`：先用 token index 缩小候选文件，再用 Roslyn 精确确认。
- `callers`：加载目标程序集和必要反向依赖程序集。
- `diagnostics`：默认只诊断指定文件或指定程序集，不默认全工程诊断。

Roslyn `Compilation` 不做磁盘序列化，只在 daemon 进程内使用 LRU 缓存。进程重启后通过快照和索引按需重建。

## 状态与诊断

`code_index status` 和 `doctor` 应输出快照模式信息：

```json
{
  "workspaceMode": "unity-snapshot",
  "snapshotExists": true,
  "snapshotVersion": 1,
  "generationId": 12,
  "assemblyCount": 11,
  "sourceFileCount": 1234,
  "buildTarget": "StandaloneWindows64",
  "unityVersion": "2019.4.40f1",
  "semantic": true,
  "stale": false
}
```

没有快照时应返回明确建议：

```text
No Unity compilation snapshot found. Open the Unity project once or run Code Index prewarm from AIBridge settings.
```

快照过期时应返回 `staleReason`，例如：

```text
sourceChanged
asmdefChanged
packageChanged
buildTargetChanged
schemaMismatch
missingReference
```

## MSBuild 移除范围

需要删除或替换：

- `Microsoft.Build.Locator`
- `Microsoft.CodeAnalysis.Workspaces.MSBuild`
- `Microsoft.Build.*`
- BuildHost-net472
- BuildHost-netcore
- `Program.RegisterMSBuild`
- `MSBuildWorkspace.OpenSolutionAsync`
- 以 `.sln/.csproj` 是否存在作为语义能力前置条件的检查逻辑

需要保留：

- `Microsoft.CodeAnalysis`
- `Microsoft.CodeAnalysis.CSharp`
- `Microsoft.CodeAnalysis.Workspaces`
- `Microsoft.CodeAnalysis.CSharp.Workspaces`

Roslyn DLL 继续放在 `Tools~/CLI/<rid>/CodeIndex/`，作为外部工具依赖，不作为 Unity 插件导入，避免 Unity 资产导入和域加载冲突。

## reset 行为

`code_index reset` 不应删除用户配置和 Unity 生成的快照。建议默认只删除：

- `status.json`
- `lock.json`
- daemon temp
- daemon 内部 index cache

如需删除 snapshot，应新增显式选项，例如：

```text
code_index reset --include-snapshot
```

## 验证计划

必须覆盖：

1. Unity 2019.4.x 项目。
2. 当前 Unity 6000 项目。
3. 没有 `.sln/.csproj` 的项目。
4. 用户机器没有 .NET SDK，只有运行 daemon 所需 runtime 或 self-contained 工具。
5. 多 asmdef 大工程。
6. 修改单个程序集源码后，仅该程序集快照和索引更新。
7. 修改被依赖程序集后，反向依赖程序集进入 `dependencyStale`。
8. `symbol`、`definition`、`references`、`callers`、`diagnostics` 均返回 `semantic=true`。

性能指标至少记录：

- snapshot 生成耗时
- snapshot 总大小
- 单程序集 snapshot 大小
- manifest 加载耗时
- warmup 耗时
- name index 构建耗时
- token index 构建耗时
- symbol 查询耗时
- references 查询耗时

## 实施阶段

第一阶段：快照基础链路

- Editor 生成分片快照。
- daemon 读取 manifest 和 assembly snapshot。
- 使用 AdhocWorkspace 构建 Project graph。
- `warmup` 返回 `semantic=true`。

第二阶段：查询迁移

- `symbol`、`definition`、`references`、`callers`、`diagnostics` 改用 snapshot workspace。
- 保证无 `.sln/.csproj` 时仍能工作。

第三阶段：性能索引

- 增加 per-assembly name index。
- 增加 per-assembly token index。
- 增加 daemon 内存 LRU 缓存。

第四阶段：移除 MSBuild

- 删除 MSBuild 相关依赖和代码。
- 删除 BuildHost 输出。
- 更新 README、README_CN、Skill 文档和 CLI help。

最终交付版本不保留 MSBuild fallback，避免兼容复杂度回流。
