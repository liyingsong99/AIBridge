# AIBridge Workflow 设计与证据校验

完整展示页在 [index.html](./index.html)。

```mermaid
flowchart LR
  A["Preflight / Skill 路由"] --> B["Mode Enter"]
  B --> C["Mode Execute"]
  C --> D["Mode Exit / SkillHandoff"]
  D --> E["Transition Preflight"]
```

- 这页用于快速理解 workflow 的入口、模式、步骤、证据校验和交接。
- HTML 版本包含更完整的流程图、表格和源码索引。
- 设计依据来自 `Templates~/Workflows/*.json`、`Tools~/AIBridgeCLI/Workflow/*.cs` 和 `Doc~/WorkflowsPanel.md`。
