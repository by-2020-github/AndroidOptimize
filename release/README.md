# release：可直接运行的构建产物

这个目录里放的是**打包好的单文件 exe**，下载下来双击就能用：
不需要安装、不需要 .NET 运行库、不需要任何其它文件（adb 与名单都已内嵌）。

---

## ⚠️ 关于把 exe 提交进 Git

这是**为了方便直接分发**而做的取舍，代价要清楚：

| 代价 | 说明 |
|---|---|
| 仓库体积 | 每提交一个版本，Git 历史就永久增加约 70MB，**删掉文件也不会让 `.git` 变小** |
| clone 变慢 | 别人克隆这个仓库要下载全部历史版本 |
| GitHub 限制 | 单文件超过 100MB 会被直接拒绝，超过 50MB 会警告 |

**推荐做法是用 GitHub Releases**（已经在 `.github/workflows/release.yml` 里配好了）：

```powershell
git tag v0.1.0
git push origin v0.1.0
```

推送 tag 后会自动编译、跑自检、把 exe 挂到 Releases 页面。Releases 的附件不占仓库体积，
用户下载体验也更好（有版本列表、下载统计）。

### 如果以后不想再往仓库里放 exe

```powershell
git rm -r --cached release          # 停止跟踪，保留本地文件
Add-Content .gitignore "`n/release/"
```

注意：这样只是停止跟踪，**已经提交过的历史里仍然有那 70MB**。要真正瘦身得重写历史
（`git filter-repo`），已经推到远端的话还需要强推——所以最好一开始就想清楚。

---

## 怎么重新生成

```powershell
.\tools\publish.ps1 -OutputDirectory release -Version 0.1.0
```

脚本会自动跑一遍 90 项自检，不通过就不产出。

> 顺便：`dist\` 是构建输出目录（不纳入版本管理），`release\` 专门放要提交的产物，两者分开。
