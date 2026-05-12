# AIBridgeProjectSettings `FilePath` 在 Unity 2019.4 不可用的解决说明

## 背景

当前问题出在 [`Packages/AIBridge/Editor/Utils/AIBridgeProjectSettings.cs`](Client\Packages\AIBridge\Editor\Utils\AIBridgeProjectSettings.cs:8)：

```csharp
[FilePath("ProjectSettings/AIBridgeSettings.asset", FilePathAttribute.Location.ProjectFolder)]
internal sealed class AIBridgeProjectSettings : ScriptableSingleton<AIBridgeProjectSettings>
```

项目 Unity 版本为 `2019.4.40`，该写法在当前工程内无法正常编译，也无法在 Unity 中使用。

## 已验证结论

已对 [`cn.lys.aibridge.Editor.csproj`](Client\cn.lys.aibridge.Editor.csproj) 做静态编译检查，得到确定性报错：

- `AIBridgeProjectSettings.cs(8,6): error CS0122: “FilePathAttribute”不可访问，因为它具有一定的保护级别`
- `AIBridgeProjectSettings.cs(8,57): error CS0122: “FilePathAttribute”不可访问，因为它具有一定的保护级别`

这说明当前问题不是：

- `using UnityEditor;` 缺失
- `asmdef` 放错位置
- 运行时程序集误引用 Editor API

而是 Unity `2019.4.40` 下的 `UnityEditor.FilePathAttribute` 对当前用户脚本不可访问。

## 根因

### 1. `FilePathAttribute` 在当前 Unity 版本下不可作为外部脚本 API 使用

`AIBridgeProjectSettings` 依赖的是 `ScriptableSingleton<T> + [FilePath(...)]` 这一套持久化方式。

但在当前项目使用的 Unity `2019.4.40` 中，`FilePathAttribute` 虽然存在于 UnityEditor 侧实现体系里，但对外部用户脚本不是可直接访问的公共 API，因此会在编译阶段直接报 `CS0122`。

### 2. 这不是程序集配置问题

[`Packages/AIBridge/Editor/cn.lys.aibridge.Editor.asmdef`](Client\Packages\AIBridge\Editor\cn.lys.aibridge.Editor.asmdef:1) 已限定在 `Editor` 平台，结构本身没有明显问题。

因此继续围绕 `asmdef`、命名空间、目录位置排查，不能解决这个问题。

## 推荐方案

### 推荐结论

不要继续依赖 `[FilePath(...)]`，改为参考 HybridCLR 在 Unity `2019.4.40` 下的同类适配方式。

在当前 Unity 2019.4 项目里，最稳妥的做法是：

1. 保留 `AIBridgeProjectSettings` 作为编辑器侧配置对象
2. 去掉 `[FilePath(...)]` 和 `ScriptableSingleton<T>` 的持久化依赖
3. 参考 HybridCLR 的 `HybridCLRSettings`，改为 `ScriptableObject + InternalEditorUtility` 手动把配置读写到 `ProjectSettings/AIBridgeSettings.asset`

这样可以同时满足：

- 兼容 Unity 2019.4
- 配置仍然是“项目级”而不是“个人机器级”
- 不需要把配置落到 `Assets/`，避免新增资源和 `.meta`
- 对现有调用点改动最小，`Instance` / `SaveSettings()` 这类入口可以基本保持不变

### 参考基准

本仓库已经引入 HybridCLR，且其包内已经有 Unity `2019.4.40` 可用的同类写法：

- [`Library/PackageCache/com.code-philosophy.hybridclr@4df417e56a/Editor/Settings/HybridCLRSettings.cs`](Client\Library\PackageCache\com.code-philosophy.hybridclr@4df417e56a\Editor\Settings\HybridCLRSettings.cs:8)

该实现的核心特征是：

- 不使用 `[FilePath(...)]`
- 设置类直接继承 `ScriptableObject`
- 路径直接写成 `ProjectSettings/HybridCLRSettings.asset`
- 通过 `InternalEditorUtility.LoadSerializedFileAndForget` / `SaveToSerializedFileAndForget` 手动读写
- 保存前用 `Directory.CreateDirectory(...)` 确保 `ProjectSettings` 路径存在

## 不推荐方案

### 方案 A：改回 `EditorPrefs`

不推荐。

原因：

- `EditorPrefs` 是机器级 / 用户级配置
- 不跟项目走
- 团队协作时不可共享
- 当前代码里已经有从旧 `EditorPrefs` 迁移到项目设置的逻辑，再退回去会和现有设计目标冲突

### 方案 B：把设置资源放到 `Assets/`

可以做，但不优先。

原因：

- 会新增可见资源文件和 `.meta`
- 容易被误提交、误移动
- 与“项目设置”语义不一致

### 方案 C：升级 Unity 版本后继续使用 `[FilePath]`

理论上可行，但当前仓库明确锁定 `Unity 2019.4.40`，升级编辑器风险远大于修正这一个配置类，不适合作为本问题的一线方案。

## 建议实现

### 目标

把 [`AIBridgeProjectSettings.cs`](Client\Packages\AIBridge\Editor\Utils\AIBridgeProjectSettings.cs:1) 改为“手动单例 + 手动序列化保存”。

### 推荐落盘路径

- `ProjectSettings/AIBridgeSettings.asset`

该路径和当前设计意图一致，只是不再通过 `[FilePath]` 自动接管，而是手动读写。

### 推荐实现方式

优先按 HybridCLR 的方式使用：

- `UnityEditorInternal.InternalEditorUtility.LoadSerializedFileAndForget`
- `UnityEditorInternal.InternalEditorUtility.SaveToSerializedFileAndForget`
- `Path.GetDirectoryName(...) + Directory.CreateDirectory(...)`

理由：

- 直接支持 `ScriptableObject` 序列化
- 可写入 `ProjectSettings`
- 不需要生成 `Assets` 下资源和 `.meta`
- 对 Unity 2019.4 更贴近编辑器工具常见做法

## 参考改法

下面是按 HybridCLR 同类实现收敛后的改造方向，核心是替换掉 `[FilePath] + ScriptableSingleton<T>`：

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace AIBridge.Editor
{
    internal sealed class AIBridgeProjectSettings : ScriptableObject
    {
        private const string SettingsFilePath = "ProjectSettings/AIBridgeSettings.asset";

        private static AIBridgeProjectSettings _instance;

        [Serializable]
        internal sealed class GifRecorderSettingsData
        {
            public int FrameCount = DefaultGifFrameCount;
            public int Fps = DefaultGifFps;
            public float Scale = DefaultGifScale;
            public int ColorCount = DefaultGifColorCount;
            public float StartDelay = DefaultGifStartDelay;
        }

        [Serializable]
        internal sealed class AssistantSelectionEntry
        {
            public string TargetId;
            public bool Selected;
        }

        public const int CurrentDataVersion = 1;
        public const int DefaultGifFrameCount = 50;
        public const int DefaultGifFps = 20;
        public const float DefaultGifScale = 0.5f;
        public const int DefaultGifColorCount = 128;
        public const float DefaultGifStartDelay = 0.1f;
        public const string DefaultScriptDirectory = "Assets/AIBridgeScripts";

        [SerializeField] private int dataVersion = CurrentDataVersion;
        [SerializeField] private bool bridgeEnabled = true;
        [SerializeField] private bool debugLogging;
        [SerializeField] private string scriptDirectory = DefaultScriptDirectory;
        [SerializeField] private GifRecorderSettingsData gifRecorder = new GifRecorderSettingsData();
        [SerializeField] private List<AssistantSelectionEntry> assistantSelections = new List<AssistantSelectionEntry>();
        [SerializeField] private bool legacyGifMigrated;
        [SerializeField] private bool legacyScriptDirectoryMigrated;
        [SerializeField] private bool autoInstallSkills = true;

        public static AIBridgeProjectSettings Instance
        {
            get
            {
                if (!_instance)
                {
                    LoadOrCreate();
                }

                return _instance;
            }
        }

        public static AIBridgeProjectSettings LoadOrCreate()
        {
            var objects = InternalEditorUtility.LoadSerializedFileAndForget(SettingsFilePath);
            _instance = objects.Length > 0
                ? objects[0] as AIBridgeProjectSettings
                : (_instance ?? CreateInstance<AIBridgeProjectSettings>());
            return _instance;
        }

        public void SaveSettings()
        {
            if (dataVersion != CurrentDataVersion)
            {
                dataVersion = CurrentDataVersion;
            }

            var directory = Path.GetDirectoryName(SettingsFilePath);
            Directory.CreateDirectory(directory);
            InternalEditorUtility.SaveToSerializedFileAndForget(
                new UnityEngine.Object[] { this },
                SettingsFilePath,
                true);
        }
    }
}
```

## 改造要点

### 1. 类继承改为 `ScriptableObject`

当前最直接的问题是 `[FilePath]`，而 `ScriptableSingleton<T>` 的自动落盘能力正是依赖这个特性。  
因此建议直接收敛为普通 `ScriptableObject`，自己维护单例生命周期。

### 2. `Instance` 接口尽量不改

当前多个调用点都直接访问：

- [`AIBridge.cs`](Client\Packages\AIBridge\Editor\Core\AIBridge.cs:197)
- [`AIBridgeLogger.cs`](Client\Packages\AIBridge\Editor\Utils\AIBridgeLogger.cs:61)
- [`AIBridgeSettingsWindow.cs`](Client\Packages\AIBridge\Editor\Tools\AIBridgeSettingsWindow.cs:259)
- [`GifRecorderSettings.cs`](Client\Packages\AIBridge\Editor\Utils\GifRecorderSettings.cs:129)

如果保留 `AIBridgeProjectSettings.Instance` 和 `SaveSettings()` 两个入口，外围业务代码通常只需要极小调整，甚至不需要调整。

### 3. `ProjectSettings` 路径直接沿用 HybridCLR 的相对路径写法

这里不需要自己再从 `Application.dataPath` 反推项目根目录。

直接保持与 HybridCLR 一致的写法即可：

- `ProjectSettings/AIBridgeSettings.asset`

理由是当前参考实现已经证明这套“相对项目根目录的 `ProjectSettings` 路径 + InternalEditorUtility 手动序列化”在 Unity `2019.4.40` 下可用，也能减少额外路径拼接逻辑。

### 4. 首次创建时要给默认值

当前字段已经通过字段初始化提供默认值，保留即可。  
首次没有设置文件时直接 `CreateInstance<AIBridgeProjectSettings>()`，即可沿用这些默认值。

## 风险点

### 1. `Save(true)` 行为会消失

原实现调用的是 `ScriptableSingleton.Save(true)`。  
改成手动序列化后，保存逻辑完全由 `SaveSettings()` 接管。

影响范围：

- 仅影响 [`AIBridgeProjectSettings`](Client\Packages\AIBridge\Editor\Utils\AIBridgeProjectSettings.cs:1) 这一类的持久化方式
- 外部调用方只要继续走 `SaveSettings()`，风险可控

### 2. 若后续新增字段，仍需保证 Unity 可序列化

例如：

- 自定义类加 `[Serializable]`
- 字段使用 Unity 支持的序列化类型

否则会出现“能运行但不落盘”的问题。

### 3. 包目录仍处于新增未提交状态

当前 `git status` 显示 `Packages/AIBridge/` 仍是未跟踪目录。  
如果后续要正式修代码，需要注意不要误带上与本问题无关的包内文件。

## 推荐执行顺序

1. 删除 [`AIBridgeProjectSettings.cs`](Client\Packages\AIBridge\Editor\Utils\AIBridgeProjectSettings.cs:8) 上的 `[FilePath(...)]`
2. 将类从 `ScriptableSingleton<AIBridgeProjectSettings>` 改为 `ScriptableObject`
3. 参考 HybridCLR 的 `HybridCLRSettings`，增加手动加载 / 保存逻辑，落盘到 `ProjectSettings/AIBridgeSettings.asset`
4. 保持 `Instance` 与 `SaveSettings()` 接口不变
5. 编译 `cn.lys.aibridge.Editor.csproj` 做静态验证
6. 进入 Unity 后手动验证设置读写是否生效

## 最终结论

这个问题的正确修法不是“继续想办法让 `[FilePath]` 在 Unity 2019.4 生效”，而是接受该版本下它对用户脚本不可用这一事实，并参考 HybridCLR 在同版本里的做法改成手动持久化。

对当前项目，推荐方案是：

- 放弃 `[FilePath]`
- 保留项目级配置语义
- 参考 HybridCLR 的 `ScriptableObject + InternalEditorUtility` 持久化模式
- 手动序列化到 `ProjectSettings/AIBridgeSettings.asset`
- 尽量保持现有调用接口不变

这是改动面最小、兼容性最稳、最符合当前 Unity 2019.4 约束的方案。
