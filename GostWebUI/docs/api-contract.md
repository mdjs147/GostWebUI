# REST API 契约

Base:`http://127.0.0.1:{WebPort}/api`,请求/响应均为 JSON。

所有请求(含静态页)校验 Host 头,仅放行 `127.0.0.1` / `localhost` / `[::1]`,其余返回 403(DNS rebinding 防护,见 `ApiServer.ValidateHostHeader`)。

**响应信封约定**:写操作(增/删/改/启停/设置)成功统一返回 `{ "ok": true, ... }`;业务失败统一返回 `{ "ok": false, "message": "..." }` 并配相应状态码(400 校验失败 / 404 不存在 / 409 冲突 / 500 内部错误)。连接测试返回 `ConnectionTestResult`(`success` 字段),日志与状态查询按各自结构返回。

## 配置

### GET /api/config
返回全局配置。`rulesPath` / `logDirectory` 为解析后的**生效绝对路径**(未配置时也返回默认位置:exe 同目录 `rules.json` / `logs`)。
```json
{
  "webPort": 31847,
  "gostPath": "gost.exe",
  "rulesPath": "C:\\Tools\\GostWebUI\\rules.json",
  "logDirectory": "C:\\Tools\\GostWebUI\\logs",
  "logRetentionDays": 7,
  "rules": [ ForwardRule, ... ]
}
```

### PUT /api/config
```json
// 请求(各字段均可省略/传 null 表示不修改)
{
  "webPort": 31847,
  "gostPath": "C:\\Tools\\gost\\gost.exe",
  "rulesPath": "D:\\data\\rules.json",
  "logDirectory": "D:\\data\\logs",
  "logRetentionDays": 7
}
// 响应
{ "ok": true, "restartRequired": true }
```
- `webPort` 传 0 表示不改端口(前端只改其他字段时用)。改端口需重启。非 0 且越界(不在 1-65535)返回 400。
- `rulesPath` 传 null 不改;传空串恢复默认位置;传新路径为**迁移语义**:把当前规则写入新位置后切换,旧文件原样保留作备份。**目标文件已存在时返回 409 拒绝**(绝不覆盖、也不隐式切换到未知规则集),需先移走目标文件或换路径。相对路径锚定程序目录。
- `logDirectory` 传 null 不改;传空串恢复默认;新目录立即创建(失败返回 409),此后新日志写入新目录,旧目录文件原样保留。
- `logRetentionDays` 传 null 不改;0 表示永久保留;越界(不在 0-3650)返回 400。保存后立即按新值清理一次过期日志。
- 迁移/路径类失败统一返回 409:`{ "ok": false, "message": "..." }`(message 可直接展示);失败字段之前的字段已生效,改正后重提交即可(幂等)。

## gost.exe 完整性(指纹锁定)

首次成功启动规则时自动锁定 gost.exe 的 SHA-256(TOFU,存 `config.json` 的 `GostSha256`);之后每次启动前校验,不一致则**拒绝启动**:`start` 返回 `{ "ok": true, "running": false }`,拒启原因写入该规则日志。前端在启动失败后查询下面的接口做归因提示(gost 缺失 / 指纹不一致)。

### GET /api/gost/integrity
```json
{
  "gostPath": "C:\\Tools\\GostWebUI\\gost.exe",
  "fileExists": true,
  "currentSha256": "ECAF1B9E...",
  "pinnedSha256": "ECAF1B9E...",
  "trusted": true
}
```
`gostPath` 为解析后的**绝对路径**(相对路径已锚定程序目录);`currentSha256` 在文件缺失/不可读时为 `null`;`pinnedSha256` 为 `null` 表示尚未锁定;`trusted` = 已锁定且与当前文件一致。

### POST /api/gost/trust
用户核实升级/更换的 gost 后调用:把锁定指纹更新为当前文件的实际指纹并落盘。
```json
// 成功
{ "ok": true, "pinnedSha256": "2E5C6FA6..." }
// 文件缺失或不可读:409
{ "ok": false, "message": "无法读取 gost.exe(...),未更新锁定指纹" }
```

## 开机启动

### GET /api/startup
```json
{ "enabled": false }
```

### PUT /api/startup
```json
// 请求
{ "enabled": true }
// 响应
{ "ok": true, "enabled": true }
```
写 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 注册表值(当前用户,无需管理员权限)。失败返回 500:`{ "ok": false, "message": "..." }`。

## 规则 CRUD

ForwardRule 结构:
```json
{
  "id": "3f2a...",            // 服务端生成;新建可省略
  "name": "41011-SOCKS5",
  "listenAddress": "127.0.0.1",
  "listenPort": 41011,
  "targetHost": "192.0.2.10",
  "targetPort": 41011,
  "proxyType": "socks5",      // socks5 | http | direct
  "proxyHost": "127.0.0.1",
  "proxyPort": 3066,
  "autoStart": false,
  "mode": "gost"              // gost(默认) | mysql
}
```

`mode` 决定转发实现:`gost`(默认,缺省即视为 gost)拉起 gost 子进程做通用 TCP 转发;`mysql` 为进程内 MySQL TLS 中继,以 SSLRequest 分段补全兼容「对首包做嗅探/懒连接」的中转并访问 server-first 的 MySQL(见 `docs/connection-test.md`),不依赖 gost.exe,出口仅支持 `direct` 或 `socks5`,且要求客户端启用 TLS(mysqlsh `--ssl-mode=REQUIRED`)。

| 方法 | 路径 | 说明 | 返回 |
| --- | --- | --- | --- |
| GET | `/api/rules` | 全部规则 | `[ForwardRule]` |
| POST | `/api/rules` | 新建(body 为 ForwardRule) | `{ "ok": true, "rule": ForwardRule }`(rule 含服务端生成的 id) |
| PUT | `/api/rules/{id}` | 更新;运行中返回 409 | `{ "ok": true, "rule": ForwardRule }` |
| DELETE | `/api/rules/{id}` | 删除(先停进程);不存在返回 404 | `{ "ok": true }` |

运行中编辑冲突:
```json
409 { "ok": false, "message": "规则运行中,请先停止再编辑" }
```

**监听地址查重**:POST / PUT 时若监听端口与其他规则相同、且监听地址互相覆盖(空 / `0.0.0.0` / `::` 视为绑定所有网卡,与任何地址冲突;其余按不区分大小写精确比较),返回 409:
```json
409 { "ok": false, "message": "监听地址与规则「41011-SOCKS5」冲突(127.0.0.1:41011)" }
```
查重与写入在服务层同一把锁内原子完成;前端 `findListenConflict` 有同语义的本地预检,仅为即时反馈,后端为准。

POST / PUT 有服务端字段校验(名称/目标地址必填、端口 1-65535、非 direct 需代理地址端口、`mode` 仅 gost/mysql 且 mysql 出口限 direct/socks5),不通过返回 400:
```json
400 { "ok": false, "message": "监听端口需在 1-65535 之间" }
```
POST 时若 body 自带的 `id` 为空、与现有规则重复、或含字母数字与 `-`/`_` 以外的字符(或超过 64 位),服务端会重新生成,以保证 Id 唯一且可安全用于 REST 路径。

## 启停与状态

| 方法 | 路径 | 说明 | 返回 |
| --- | --- | --- | --- |
| POST | `/api/rules/{id}/start` | 不存在返回 404 | `{ "ok": true, "running": true }` |
| POST | `/api/rules/{id}/stop` | 不存在返回 404;停止幂等 | `{ "ok": true, "running": false }` |
| POST | `/api/rules/start-all` | 启动全部规则(后端遍历,幂等) | `{ "ok": true, "total": 3, "running": 3 }` |
| POST | `/api/rules/stop-all` | 停止全部规则(并行停止,幂等) | `{ "ok": true, "total": 3, "running": 0 }` |
| GET | `/api/rules/{id}/status` | 不存在返回 404(前端据此发现规则已在别处被删并重拉列表) | `{ "id": "...", "running": true }` |

`start` 成功但 gost 被拒启/未能拉起时返回 `{ "ok": true, "running": false }`(拒启原因写入规则日志);`start-all` 的 `running < total` 同理。前端「全部启动/全部停止」调用批量端点,不再依赖本地运行状态快照逐条调用(快照过期会漏启/漏停)。

## 日志(增量拉取)

### GET /api/rules/{id}/logs?afterSeq={n}
返回序号大于 `afterSeq` 的日志;`afterSeq=0` 取当前缓冲全部(最多 500 行)。
```json
[ { "seq": 128, "ruleId": "...", "time": "14:03:21", "text": "..." }, ... ]
```
前端记录已见最大 `seq`,轮询时带上,实现增量追加。

本接口只读内存环形缓冲(进程重启即清空)。持久化归档由 `LogFileService` 独立完成:同一批日志同时按天写入日志目录下的 `gost-yyyyMMdd.log`(行格式 `yyyy-MM-dd HH:mm:ss [规则名] 文本`),超过保留天数自动删除;文件不经 API 暴露,用户直接到目录查看。

## 连接测试

### POST /api/test/proxy — 测代理端口 TCP 可达
```json
{ "host": "127.0.0.1", "port": 3066, "timeoutMs": 5000 }
```

### POST /api/test/target — 经 SOCKS5 端到端(最有用)
```json
{ "proxyHost": "127.0.0.1", "proxyPort": 3066, "targetHost": "192.0.2.10", "targetPort": 41011, "timeoutMs": 5000 }
```

### POST /api/test/listener — 测本地监听端口是否已起
```json
{ "host": "127.0.0.1", "port": 41011, "timeoutMs": 5000 }
```
`host` 传规则的监听地址;省略或传 `0.0.0.0`(全网卡)时按 `127.0.0.1` 探测。

三者统一返回 `ConnectionTestResult`:
```json
{ "success": true, "elapsedMs": 42, "message": "链路通:经 127.0.0.1:3066 已连到 192.0.2.10:41011" }
```

## 健康检查
### GET /api/health → `{ "ok": true, "version": "2.1.0" }`
`version` 为应用版本号,单一来源是 csproj 的 `<Version>`(运行时经 `Core/AppVersion.cs` 从程序集特性读取);前端顶栏据此显示 `v2.1.0` 字样,托盘悬停提示同源。
