# 参与贡献

感谢你帮助改进 LittleCalendar。

## 报告问题

请说明 Windows 版本、操作步骤、预期结果和实际结果。可以附上相关日志片段，但务必先遮盖邮箱地址、姓名、公司、邮件主题、Message-ID、API Key、授权码和其他个人信息。不要上传完整的 `calendar.json`、`mail-sync.json` 或真实邮件。

## 开发改动

1. 从 `main` 创建简短、明确的分支，例如 `fix/mail-deduplication`。
2. 阅读 [AGENTS.md](AGENTS.md)，确认改动没有越过隐私和邮件权限边界。
3. 先写能复现问题或描述行为的失败测试，再实现最小修改。
4. 测试必须使用虚构邮箱、邮件和密钥，并写入隔离的临时数据目录。
5. 不提交构建输出、依赖缓存、日志、用户数据、EXE、DLL 或 ZIP。

构建和运行完整测试：

```powershell
.\restore-packages.ps1
.\build.ps1 -Test -OutputDirectory test-output
```

提交 Pull Request 时，请说明：

- 用户可见的变化；
- 新增或修改了哪些测试；
- 是否涉及本地数据迁移、网络请求、邮件状态或隐私边界；
- 手工验证方法及结果；
- 若界面发生变化，附上已遮盖个人信息的截图。

贡献内容将按仓库的 MIT License 发布。
