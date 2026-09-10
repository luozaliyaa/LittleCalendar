# Chat Assistant Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a local chat assistant that can strictly incrementally sync new mail and answer deadline-aware questions from the calendar.

**Architecture:** A deterministic `ChatIntentRouter` maps supported commands to typed local operations. `ChatAssistantService` owns orchestration, while IMAP access remains inside `MailSyncCoordinator` and DeepSeek only analyzes fetched mail or phrases bounded calendar facts. Chat history is stored separately and atomically, capped at 50 messages.

**Tech Stack:** C# 7-compatible code on .NET Framework 4.6.2, WPF, MailKit, `JavaScriptSerializer`, existing DeepSeek HTTP client, PowerShell build scripts.

**Spec:** `docs/superpowers/specs/2026-09-10-chat-assistant-design.md`

## Global Constraints

- Keep .NET Framework 4.6.2 compatibility.
- Store only the latest 50 chat messages locally; never store credentials, full mail bodies, full prompts, or raw model responses in chat history.
- Do not send, delete, or move mail, and do not let chat complete, delete, or arbitrarily create todos.
- Use per-folder UID as the primary incremental cursor and ISO 8601 timestamps with offsets for display and diagnostics.
- Run background work off the WPF UI thread and marshal UI updates through `Dispatcher`.
- Preserve the existing manual day-window rescan behavior in Settings.

---

### Task 1: Versioned local chat history

**Files:**
- Create: `ChatModels.cs`
- Create: `ChatStore.cs`
- Modify: `build.ps1`
- Test: `Tests.cs`

**Interfaces:**
- Produces: `ChatMessage`, `ChatHistory`, and `ChatHistoryStore` with `Load()`, `Save(ChatHistory)`, `Append(ChatMessage)`, and `Clear()`.
- Consumes: the repository's temporary-file-plus-replace persistence pattern from `MailStore.cs`.

- [ ] **Step 1: Write failing persistence tests**

Add tests that append 55 alternating user/assistant messages, assert only messages 6-55 remain in chronological order, corrupt the primary JSON and assert backup recovery, and assert serialized content contains none of `deepseek-secret`, `mail-auth-code`, or a supplied raw mail body.

- [ ] **Step 2: Run the test suite and verify the new types are missing**

Run: `./build.ps1 -Test -OutputDirectory test-chat-store-red`

Expected: compilation fails because `ChatHistoryStore` and `ChatMessage` do not exist.

- [ ] **Step 3: Implement models and atomic storage**

Create these public contracts:

```csharp
public sealed class ChatMessage {
    public string Id { get; set; }
    public string Role { get; set; }
    public string Text { get; set; }
    public string CreatedAt { get; set; }
    public string Intent { get; set; }
    public List<string> TodoIds { get; set; }
    public ChatSyncSummary Sync { get; set; }
}

public sealed class ChatHistory {
    public int Version { get; set; }
    public List<ChatMessage> Messages { get; set; }
}

public sealed class ChatHistoryStore {
    public ChatHistoryStore(string directory);
    public ChatHistory Load();
    public void Save(ChatHistory history);
    public void Append(ChatMessage message);
    public void Clear();
}
```

Validate roles to `user`, `assistant`, or `system`; trim display text to 4,000 characters; normalize timestamps with `DateTimeOffset`; and retain `Messages.Skip(Math.Max(0, count - 50))` before saving. Follow `MailStateStore.Save` for atomic replacement and backup recovery.

- [ ] **Step 4: Include the new files in both app and test compilation**

Update the source arrays in `build.ps1` so `ChatModels.cs` and `ChatStore.cs` are compiled before their consumers.

- [ ] **Step 5: Run tests**

Run: `./build.ps1 -Test -OutputDirectory test-chat-store-green`

Expected: all tests pass, including retention and recovery tests.

- [ ] **Step 6: Commit**

```powershell
git add ChatModels.cs ChatStore.cs Tests.cs build.ps1
git commit -m "feat: add local chat history storage"
```

### Task 2: Deterministic task queries and chat intent routing

**Files:**
- Create: `ChatAssistant.cs`
- Modify: `build.ps1`
- Test: `Tests.cs`

**Interfaces:**
- Consumes: `CalendarData`, `Todo`, `Deadlines.End`, and `Dates.Parse`.
- Produces: `ChatIntentRouter.Parse(string)`, `TaskQueryService.Query(CalendarData, DateTime, TaskQueryRange)`, `TaskQueryResult`, and `ChatIntent`.

- [ ] **Step 1: Write failing intent and date-boundary tests**

Cover exact and natural variants of “读取新邮件”, “同步一下邮箱”, “今天要做什么”, “明天截止”, and “未来七天”; assert unrelated text becomes `chat`. Add calendar fixtures for one overdue deadline, one today deadline, one tomorrow ordinary todo, one cross-date deadline ending within seven days, one completed item, and one deleted item.

- [ ] **Step 2: Run tests and confirm missing router/query failures**

Run: `./build.ps1 -Test -OutputDirectory test-chat-router-red`

Expected: compilation fails because `ChatIntentRouter` and `TaskQueryService` do not exist.

- [ ] **Step 3: Implement typed routing and local querying**

Use these contracts:

```csharp
public enum ChatIntentKind { SyncMailIncremental, ListTasks, Chat }
public enum TaskQueryRange { Today, Tomorrow, SevenDays, FourteenDays }
public sealed class ChatIntent { public ChatIntentKind Kind; public TaskQueryRange Range; }
public sealed class TaskQueryResult { public List<Todo> Overdue; public List<Todo> Due; public string DeterministicText; }
```

Normalize whitespace and case, route explicit sync phrases before task phrases, and calculate due instants locally. Exclude completed and deleted items. Cap model-facing results at 50 while keeping deterministic sorting by overdue status, deadline, importance, and title.

- [ ] **Step 4: Add the source file to the build**

Compile `ChatAssistant.cs` for both production and tests without introducing a new dependency.

- [ ] **Step 5: Run tests**

Run: `./build.ps1 -Test -OutputDirectory test-chat-router-green`

Expected: all intent and date-boundary tests pass.

- [ ] **Step 6: Commit**

```powershell
git add ChatAssistant.cs Tests.cs build.ps1
git commit -m "feat: add safe chat intent and task queries"
```

### Task 3: Strict incremental mail synchronization with diagnostics

**Files:**
- Modify: `MailModels.cs`
- Modify: `MailSync.cs`
- Modify: `MailClient.cs`
- Test: `Tests.cs`

**Interfaces:**
- Consumes: existing `MailSyncCoordinator`, `MailFetchRequest`, `MailSyncState`, and per-folder `MailFolderState`.
- Produces: `MailSyncMode`, `MailSyncCoordinator.RunIncremental()`, and result cursor details exposed through `MailSyncResult.Folders`.

- [ ] **Step 1: Write failing incremental cursor tests**

Seed INBOX with `UidValidity=7`, `LastUid=42`, and `LastScannedAt=2026-09-10T08:00:00+08:00`; assert incremental sync requests UID 43, exposes old/new UID and scan timestamps, and advances only after all returned messages succeed. Add tests for first sync defaulting to seven days, UIDVALIDITY change resetting to a seven-day recovery scan, and a failed message leaving the cursor retryable.

- [ ] **Step 2: Run tests and verify the incremental API is absent**

Run: `./build.ps1 -Test -OutputDirectory test-chat-mail-red`

Expected: compilation fails because `RunIncremental` and folder cursor summaries do not exist.

- [ ] **Step 3: Separate manual rescan from strict incremental mode**

Add:

```csharp
public enum MailSyncMode { ManualWindow, AutomaticIncremental, ChatIncremental }
public MailSyncResult RunIncremental();
```

Build `MailFetchRequest.MinimumUid` from `LastUid + 1` for both incremental modes. Use the saved manual window, default seven days, only when no cursor exists or UIDVALIDITY changed. Keep `Run(false, days)` as `ManualWindow` so Settings continues to reanalyze the selected date range.

- [ ] **Step 4: Expose safe cursor summaries**

Add `MailFolderSyncSummary` containing folder display name, prior UID, next requested UID, final UID, prior scan timestamp, completed timestamp, fetched count, and an optional sanitized error. Do not include subjects, senders, bodies, account addresses, or Message-IDs.

- [ ] **Step 5: Run tests**

Run: `./build.ps1 -Test -OutputDirectory test-chat-mail-green`

Expected: all tests pass; existing manual window and automatic sync tests remain green.

- [ ] **Step 6: Commit**

```powershell
git add MailModels.cs MailSync.cs MailClient.cs Tests.cs
git commit -m "feat: expose strict incremental mail sync"
```

### Task 4: Bounded DeepSeek chat responses with local fallback

**Files:**
- Modify: `Agent.cs`
- Modify: `ChatAssistant.cs`
- Test: `Tests.cs`

**Interfaces:**
- Consumes: `IWorkAgent`, `SecretStore`, `TaskQueryResult`, and `CalendarData` copies.
- Produces: `IChatLanguageAgent.Reply(ChatLanguageRequest, string apiKey, string model)` and `ChatAssistantService.Handle(string, DateTime)`.

- [ ] **Step 1: Write failing orchestration and safety tests**

Assert task questions work without an API key using `DeterministicText`; with a fake language agent, assert the request contains only bounded todo fields and no mail body. Assert a mail-sync intent calls `RunIncremental` exactly once, a general chat intent never invokes IMAP, and model failure returns the local task result instead of losing the response.

- [ ] **Step 2: Run tests and verify the service contracts are missing**

Run: `./build.ps1 -Test -OutputDirectory test-chat-service-red`

Expected: compilation fails because `IChatLanguageAgent` and `ChatAssistantService` do not exist.

- [ ] **Step 3: Implement the bounded language request**

Define a strict request containing the current local time, timezone, user question, range label, and at most 50 items with title, due instant, importance, deadline confirmation, and a 500-character note excerpt. Instruct DeepSeek to return strict JSON:

```json
{"answer":"今天有 2 项需要处理。","todoIds":["id-1","id-2"]}
```

Parse and validate `answer` and IDs; drop IDs not present in the supplied query. Do not include mail bodies or expose arbitrary tool names to the model.

- [ ] **Step 4: Implement assistant orchestration and persistence**

Append the user message before work starts. For task queries, get deterministic facts first and optionally phrase them through DeepSeek. For incremental mail sync, call `RunIncremental`, format a local result containing timestamps and folder cursors, then append the assistant response with `ChatSyncSummary`. For ordinary chat, provide only bounded current-calendar context. Append the final assistant message through `ChatHistoryStore`.

- [ ] **Step 5: Run tests**

Run: `./build.ps1 -Test -OutputDirectory test-chat-service-green`

Expected: all orchestration, fallback, prompt-boundary, and persistence tests pass.

- [ ] **Step 6: Commit**

```powershell
git add Agent.cs ChatAssistant.cs Tests.cs
git commit -m "feat: add bounded DeepSeek chat orchestration"
```

### Task 5: WPF conversation window and runtime integration

**Files:**
- Modify: `Desktop.cs`
- Modify: `Program.cs`
- Modify: `Theme.xaml`
- Test: `Tests.cs`

**Interfaces:**
- Consumes: `ChatAssistantService`, `ChatHistoryStore`, `CalendarRuntime`, and `CalendarWindow`.
- Produces: `ChatWindow`, `CalendarWindow.ChatRequested`, and asynchronous `CalendarRuntime.OpenChat()` / `SendChatMessage(string)` integration.

- [ ] **Step 1: Write failing UI structure tests**

Construct the main window and assert a “对话助手” button exists. Construct `ChatWindow` with fake history and assert accessible controls named “对话消息列表”, “消息输入框”, “发送消息”, and “清空对话” exist, plus the four shortcut buttons. Trigger a fake send and assert the UI disables duplicate sends, then re-enables after completion.

- [ ] **Step 2: Run tests and verify missing UI failures**

Run: `./build.ps1 -Test -OutputDirectory test-chat-ui-red`

Expected: tests fail because the entry button and `ChatWindow` do not exist.

- [ ] **Step 3: Build the conversation UI**

Add a non-modal, owner-centered window with a virtualizable scroll area, distinct user/assistant bubbles, selectable text, an input box accepting Enter to send and Shift+Enter for a newline, shortcut chips, progress text, and an empty state explaining supported commands. Render cursor/time summaries in a compact bordered card beneath sync responses.

- [ ] **Step 4: Integrate background execution safely**

Create a single `ChatWindow` per runtime and reuse it while open. Execute service calls on `ThreadPool`; use `Window.Dispatcher.BeginInvoke` to add the response and refresh the calendar. Share the existing mailbox busy gate so Settings, automatic sync, and chat cannot start concurrent IMAP sessions.

- [ ] **Step 5: Add local history clearing and todo navigation**

Require an in-window confirmation before clearing. Clicking a response todo link selects its date in the main window and brings the calendar forward; it does not mutate the item.

- [ ] **Step 6: Run tests and isolated smoke launch**

Run: `./build.ps1 -Test -OutputDirectory test-chat-ui-green`

Run: `./build.ps1 -OutputDirectory test-chat-smoke`

Run: `./test-chat-smoke/LittleCalendar.exe --data-dir ./test-chat-smoke-data`

Expected: tests pass; the app opens, the chat window opens once, local task queries render, and closing/reopening restores messages.

- [ ] **Step 7: Commit**

```powershell
git add Desktop.cs Program.cs Theme.xaml Tests.cs
git commit -m "feat: add calendar chat window"
```

### Task 6: Documentation, repository hygiene, and release verification

**Files:**
- Modify: `README.md`
- Modify: `AGENTS.md`
- Modify: `CONTRIBUTING.md`
- Test: `Tests.cs`

**Interfaces:**
- Consumes: all user-facing behavior completed in Tasks 1-5.
- Produces: contributor guidance and verified release-ready source.

- [ ] **Step 1: Add documentation assertions**

Add repository tests asserting README documents “对话助手”, strict UID incremental behavior, ISO 8601 timestamps, local 50-message retention, and the no-send/delete/move boundary.

- [ ] **Step 2: Run tests and verify documentation assertions fail**

Run: `./build.ps1 -Test -OutputDirectory test-chat-docs-red`

Expected: documentation assertions fail before the new sections are added.

- [ ] **Step 3: Document user and contributor behavior**

Explain the four shortcuts, the distinction between strict chat incremental sync and Settings day-window rescan, why UID is primary while timestamps remain visible, local history retention/clearing, DeepSeek fallback, and security boundaries. Update `AGENTS.md` module responsibilities for the new chat files.

- [ ] **Step 4: Run full verification**

Run: `./build.ps1 -Test -OutputDirectory test-chat-final`

Run: `./scripts/Test-RepositoryHygiene.ps1`

Run: `git diff --check`

Expected: the complete test suite passes, hygiene reports no credentials or generated artifacts, and diff check has no whitespace errors.

- [ ] **Step 5: Inspect release contents**

Run the repository's release packaging script into an ignored temporary output directory and inspect the archive list. Expected: the package contains the application and required runtime DLLs, but no tests, logs, chat history, mailbox state, credentials, or user data.

- [ ] **Step 6: Commit**

```powershell
git add README.md AGENTS.md CONTRIBUTING.md Tests.cs
git commit -m "docs: explain the local chat assistant"
```

