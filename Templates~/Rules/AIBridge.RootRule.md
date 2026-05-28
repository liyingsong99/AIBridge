---
templateId: unity-integration
assistant: aibridge
version: 7
target: root-rule
---
## AIBridge Bootstrap

**CLI Alias**: `$CLI = {{CLI_PATH}}`

**{{COMMON_COMMANDS_TITLE}}**:
```bash
$CLI compile unity
$CLI get_logs --logType Error
$CLI editor log --message "Hello" --logType Warning
```

**{{PROJECT_VERSION_TITLE}}**:
- {{UNITY_VERSION_RULE}}
- {{CSHARP_VERSION_RULE}}

**{{CAPABILITIES_TITLE}}**:
- {{CODE_INDEX_CAPABILITY_RULE}}

**{{ROUTING_TITLE}}**:
- {{QUICK_TASK_RULE}}
- {{DEVELOPMENT_TASK_RULE}}
- {{WORKFLOW_SKILL_RULE}}

**{{SKILL_LOADING_TITLE}}**:
- {{WORKFLOW_SKILL_ENTRY}}
- {{SKILL_ROOT_RULE}}
