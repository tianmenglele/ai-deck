# 内嵌 UI 提示音说明（DOLLARS 风格电钢柔和乐句）

本目录内嵌 5 路程序集 `EmbeddedResource` 提示音，统一格式为 `44100 Hz / 16-bit / 单声道 PCM RIFF/WAVE`，已完成去直流偏移、温和峰值归一至约 `-4.5 dBFS`（`peak ≈ 0.596` 不削波不炸耳）及首尾余弦淡入淡出，并叠加柔和空间混响使尾音自然衰减。

| 文件 | 触发场景（`SoundKind`） | 时长 | 听感特点 |
|---|---|---|---|
| `dispatch_flight.wav` | 派单出发（`DispatchFlight`） | `1.12 s` | 清澈灵动电钢双音上行发信回响 |
| `status_change.wav` | 状态/重修轻点（`StatusChange`） | `1.00 s` | 优雅克制柔和二音轻点 |
| `work_started.wav` | 开工提醒（`WorkStarted`） | `1.28 s` | 轻柔三音微琶音频道上线感 |
| `task_completed.wav` | 交付完成（`TaskCompleted`） | `1.68 s` | 温暖明亮日系都市 Maj9 四音上行琶音 |
| `verify_passed.wav` | 验收通过（`VerifyPassed`） | `2.10 s` | 清透电钢五音和弦琶音与悠长空间混响余韵 |

- 总内嵌体积：约 `633 KB`（单文件发布后 100% 离线可用，无外部路径依赖）。
- 许可：本目录音频资产按 **CC0-1.0** 公有领域许可分发。
