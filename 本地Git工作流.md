# dnSpyEx 本地 Git 工作流

上游 `dnSpyEx/dnSpy` 为只读（无推送权限），本地提交**仅本地使用 + 推送到自己的 fork 备份**。

## 仓库布局

| 仓库 | origin（push 目标） | upstream（pull 目标） |
|---|---|---|
| 主仓库 dnSpyEx | `Ayinyue/dnSpy` | `dnSpyEx/dnSpy` |
| ILSpy | `Ayinyue/ILSpy` | `dnSpyEx/ILSpy` |
| NRefactory | `Ayinyue/nrefactory` | `dnSpyEx/nrefactory` |
| Mono.Debugger.Soft | `Ayinyue/Mono.Debugger.Soft` | `dnSpyEx/Mono.Debugger.Soft` |
| netcorefiles | `Ayinyue/netcorefiles` | `dnSpyEx/netcorefiles` |

`.gitmodules` 中所有 url 仍指向官方 `dnSpyEx/*`，**不影响构建**；fork 仅用于备份与推送。

## 日常跟进上游

```bash
# 1. 先确保工作树干净（本地构建工具等改动请先提交）
git status

# 2. 拉取上游
git fetch upstream

# 3. 把本地提交 rebase 到上游之上（pull.rebase=true 已配置，等价于 git pull）
git rebase upstream/master

# 4. 推送备份到自己的 fork
git push origin master
```

## 子模块的跟进流程（易错）

子模块的 `.gitmodules` 指向官方，所以**跟进上游时子模块不会自动更新**，需手动处理：

```bash
cd <子模块目录>
git fetch upstream && git rebase upstream/master   # 或 upstream/dnSpy（Mono 用 dnSpy 分支）
git push origin master
# 回到主仓库，提交更新后的指针
cd -
git add <子模块路径> && git commit -m "Update <name> submodule"
```

各子模块的目标分支：

- ILSpy → `ilspyv2`
- Mono.Debugger.Soft → `dnSpy`
- NRefactory / netcorefiles → `master`

## 重要陷阱

1. **子模块 rebase 可能与上游冲突**。Mono.Debugger.Soft 的 CS9258 修复即如此：上游 `d12451c` 已用 `return @field;` 解决同一问题。冲突时先读冲突文件确认双方改动，**不要直接 `--skip` 或 `--ours`**。

2. **rebase 后务必更新超级仓库的子模块指针并提交推送**，否则指针会指向不可达提交，他人 clone 会失败。

3. **判断"本地是否有独有提交"不能用 `git submodule status` 的 `+` 号**。`+` 只表示"检出位置 ≠ 超级仓库记录"，本地提交可能本来就在上游。正确做法：

```bash
git fetch <fork-url> <branch>:refs/remotes/fk/<branch>
git merge-base --is-ancestor <local-head> <remote-tip>   # 本地是否落后
git rev-list --count refs/remotes/fk/<branch>..HEAD       # 本地独有提交数
```

4. **detached HEAD 的子模块，提交前必须先建分支**，否则提交成为 dangling commit，`git gc` 后永久丢失：

```bash
git checkout -b fix/<描述>
```

5. **推送超时先区分失败类型**：
   - `schannel: failed to receive handshake` → 网络层，非凭据问题，重试即可
   - `Permission denied to <其他账号>` → 凭据管理器缓存了错误账号，在 URL 里写死用户名：`https://<user>@github.com/...`
   - 输出停在 `Pushing to ...` 且 `rc=0` → GUI 凭据窗口在后台环境无法弹出，需在交互终端执行

6. **推送后务必 `ls-remote` 验证**，静默失败（`rc=0` 但远端未变）确实发生过。

## 本地安全网

`D:\Codebase\dnSpyEx\_local_backup_20261014\` 存放 `git bundle` 备份（超级仓库 + 各子模块，含 `complete history` 验证）。

由于本地提交是唯一副本，**新增提交后应重新打包**：

```bash
git bundle create <路径>.bundle --all
git bundle verify <路径>.bundle    # 必须显示 "records a complete history"
```

注意：`git bundle verify` **必须在 Git 仓库目录内运行**，否则报 `need a repository to verify a bundle`，会误判为 INCOMPLETE。用 `git -C <repo> bundle verify` 显式指定。

## 构建验证

改动反编译器或子模块后，跑三个目标（**缺一不可**，net48 与 net10 独立编译）：

```powershell
$env:DNSPY_BUILD_ARGS = '-NoMsbuild net';        & 'Build\build-log.ps1' -Script 'build.ps1'
$env:DNSPY_BUILD_ARGS = '-NoMsbuild netframework'; & 'Build\build-log.ps1' -Script 'build.ps1'
$env:DNSPY_BUILD_ARGS = '-NoMsbuild net-x64';      & 'Build\build-log.ps1' -Script 'build.ps1'
```

产物真实路径带 `bin/` 中间层：`dnSpy/dnSpy/bin/Release/<TFM>/bin/<dll>`；自包含为 `.../win-x64/bin/` 与 `.../win-x64/publish/bin/`。
