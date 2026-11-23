# ai-collab-hub (自动纳管工程) · 项目协作总纲

- 规则目录：`.cursor/rules`
- 技能目录：`.cursor/skills`
- 共读经验库：`.cursor/memory`

<!-- AI-COLLAB-HUB:START -->
## 多 AI 协同总线与主脑实操验收闭环入口（由多 AI 协同中心维护）
- **当前唯一任务活卡**：`work/collab/TASK.md`；最近行级变更单：`work/collab/LATEST.diff`；在岗花名册：`work/collab/ROSTER.json`。
- **当前统领主脑 AI**：**Antigravity**（负责深度分析问题、自动适配各 AI 的模型与高/中/低档位、派发实操修改任务、严格验收并在不合格时打回重做直至彻底完成）。
- **共享技能直达目录**：`.cursor/skills`（遇到对应场景须先打开对应子目录下的 `SKILL.md` 执行）。
- **共读错题与经验库**：`.cursor/memory`（一处记坑、全员免疫）。
- **【强制默认铁律】全入口汇总主脑 ➔ 派给其他 AI 实操改工程 ➔ 主脑循环验收打回 ➔ 最终交付**：
  无论用户在哪个 AI 工具发起任务，均通过 `.cursor/skills/multi-ai-auto-dispatch/SKILL.md` 调用 `powershell -NoProfile -ExecutionPolicy Bypass -File "C:/Project/Godot/配套工具/tools/ai-collab-hub/启动协同中心.ps1" dispatch --project-dir "C:/Project/Godot/配套工具/tools/ai-collab-hub" --agent auto --session auto --task "请直接落地修改工程文件完成：..."`，由主脑自动匹配最适合的执行 AI 与其高/中/低档位模型去直接动手改代码/改设置/改预制体，执行后由主脑验收（不合格自动打回重做），彻底完成后再向用户交付！
- **当前已启用协同成员、权限与三档模型配置**：
  - **Antigravity**：允许快照护航直接改工程文件 · [高档:Orchestrator (Neon) | 中档:Gemini 4 Neon | 低档:Gemini 4 Hydrogen]
  - **Cursor**：允许快照护航直接改工程文件 · [高档:Claude Opus 5.5 (claude-opus-5-5) | 中档:Auto (default) | 低档:Claude Opus 5.5 (claude-opus-5-5)]
  - **Grok Bot (桌面端)**：允许快照护航直接改工程文件 · [高档:客户端默认托管（禁止切换模型·防风控） | 中档:客户端默认托管（禁止切换模型·防风控） | 低档:客户端默认托管（禁止切换模型·防风控）]
<!-- AI-COLLAB-HUB:END -->
