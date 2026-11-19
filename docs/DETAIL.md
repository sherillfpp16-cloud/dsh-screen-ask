# 屏幕问答（dsh-screen-ask）

按住 **X** 键不放，再用左键拖出一个矩形，松开左键即完成截图；松开 X 或右键/Esc 取消。截图会出现在 DSH 右下角的「屏幕问答」悬浮窗里；输入问题（可留空）后点「解答」，模型结合截图回答。悬浮窗里的「思考」下拉框只影响这一次请求（可选 off / high / max），不影响主会话。

## 工作方式

- **截图助手**：`helper/ScreenSnap.exe`（.NET/WinForms，无控制台窗口），用低级钩子监听 X 键和鼠标左键，显示全屏半透明选区，把选区存成 PNG，并往 `data/events.jsonl` 追加事件。
- **后台 keeper**：`helper/Launcher.exe` + 计划任务 `ScreenAskHelperWatch`（每分钟）。助手必须在 **DSH 进程树之外** 运行，否则会继承宿主沙箱，低级输入钩子收不到任何事件；任务计划把助手拉进用户自己的会话。
- **Host 半**：`index.js` 尾随事件文件、注册 `/screen-ask/*` 路由（state / image / dismiss / ask），把截图存成附件后调用 `ctx.llm.stream`，返回文本答案。
- **Client 半**：`client.js` 在 `shell.overlay` 注册悬浮面板，轮询 `/screen-ask/state`，自动弹出新截图，展示预览/问题/思考档位/答案。

## 配置（`cordis.patch.yml` 的插件 config）

| 键 | 含义 |
|---|---|
| `holdKey` | 触发键，`x` 或 `side`（鼠标侧键） |
| `sideButton` | `holdKey=side` 时的侧键编号，1=后退，2=前进 |
| `provider` / `model` | 模型路由，默认 `deepseek-official` / `deepseek-flash` |
| `reasoningEffort` | 默认思考档位 |
| `efforts` | 下拉框里的档位列表，默认 off/high/max |
| `outDir` | 截图与事件目录，默认 `D:\laiai\Documents\dsh-screen-ask\data` |
| `prompt` | 默认提问前缀 |

## 已知限制

- 答案目前只显示在悬浮窗里，**还没有写回当前会话**（下一步）。
- 只在 Windows 上有效；多显示器按虚拟屏幕物理像素取坐标。
- 助手进程由计划任务守护；如果手工结束它，最多 1 分钟后会重新启动。