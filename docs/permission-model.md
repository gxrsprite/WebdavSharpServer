# 权限模型（对齐 hacdias/webdav）

本文锁定 v1 语义。凡与 hacdias 不一致处，以本节说明为准。

## 1. 权限字母

| 字母 | 含义 | 对应动词（v1，与参考 `Allowed()` 对齐） |
|---|---|---|
| `C` | Create（创建） | PUT（新文件）、MKCOL、COPY/MOVE 目标（不存在时）、LOCK（路径不存在时） |
| `R` | Read（读取） | GET、HEAD、PROPFIND、COPY 源；**MOVE 源要求 R+D** |
| `U` | Update（更新） | PUT（已存在文件）、PROPPATCH、LOCK（路径存在时）、COPY/MOVE 目标（已存在时） |
| `D` | Delete（删除） | DELETE、MOVE 源（与 R 共同要求） |
| `none` | 显式拒绝 | 覆盖之前的一切允许 |

大小写不敏感。`LOCK` 按 hacdias 计为写：路径存在要 `U`，不存在要 `C`；
`UNLOCK` 要求 `C` 或 `U`。虚拟根不可写、挂载点不可删（对齐参考 `multidir`：
写虚拟根 403、删挂载点 403、MKCOL 已存在路径 405）。

## 2. 生效顺序：最后一条命中生效

求值 = `Evaluate(虚拟路径, 默认权限, 有序规则)`：

1. 起点 = 用户默认权限（`Dav:DefaultPermissions`，默认 `R`）。
2. 适用规则 = 全局规则 + 该用户所属角色的规则 + 该用户专属规则，
   **统一按 `SortOrder` 从小到大扁平排序**后依次应用。
3. **最后一条命中的规则生效**（与 hacdias “last rule that matches wins” 一致；
   `SortOrder` 跨目标可比——拒绝规则只要序号最大就能覆盖管理员的放行规则）。

> 典型配置：`admin` 角色 `/ → CRUD`（SortOrder=100），
> 再用一条用户级 `/public/private/ → none`（SortOrder=900）抠掉黑洞目录。

## 3. path vs regex

- 请求路径在鉴权前先规范化（对齐参考 `cleanPath`）：消解 `.`/`..` 段，
  `/c/`、`/c/.`、`/c/..` 都视为集合自身（保留尾斜杠）；`..` 越过虚拟根直接 `403`。
  规范化后的路径才用于挂载解析与规则匹配，因此 `/allowed/../denied/x`
  会按 `/denied/x` 命中规则——点段不能绕过鉴权。
  `Request.Path` 只由服务器解码一次，不二次解码（防 `%252e` 双重解码绕过）；
  物理映射层的 `ResolveSafePath` 作为第二道防线保留。
- `path`：**前缀匹配**。尾斜杠同时覆盖集合自身：
  `/secret/` 覆盖对 `/secret` 的请求，也覆盖 `/secret/…`；
  但作用于集合自身还要求父级权限（hacdias 同理：v1 简化为只看本路径求值）。
- `regex`：对路径**字面匹配**，无前缀/集合特殊处理。
  想覆盖集合自身请写 `^/secret(/|$)`。
- 点段已规范化（`/public/../secret` 按 `/secret` 匹配）；
  `..` 逃逸由 `DavPathSecurity` 在映射物理路径时直接拦截（403）。

## 4. 大小写

对齐 hacdias（`casefold.go` + `hasCaseInsensitiveBacking`）：**按本次命中的 backing
物理根逐请求探测**——翻转末级名称首个可翻转字母后依然存在即判不敏感；
探测失败（路径不存在/无字母可翻）回退平台默认（Windows/macOS 不敏感）。
Fold = 小写化 + Unicode NFC；不敏感时 regex 也按折叠语义匹配。

与参考的差异：参考用 `os.SameFile` 排除“大小写敏感卷上恰好同时存在两种拼写”
的极端碰撞，本实现用存在性判定（该场景会误判为不敏感——偏向**拒绝**侧，
deny 规则依然生效，fail-closed）。各挂载可处不同卷，策略天然分挂载生效；
管理页增删挂载时刷新探测缓存。

## 5. 多路径操作

- 集合 PROPFIND：无 `R` 的条目直接隐藏（hacdias：listings leave out denied entries）。
- COPY 集合：**过滤式部分拷贝**——无 `R` 的条目被留下，不整体拒绝
  （对齐参考 `permissionsFS`：COPY 经由过滤后的列表触及后代）。
- MOVE / DELETE 集合：**整树检查**（对齐参考 `allowedThroughout`，走未过滤物理枚举）——
  任一后代缺要求权限（MOVE：`R+D`；DELETE：`D`）即整体 403。
- COPY/MOVE 目标：不存在→要求 `C`，已存在→文件要求 `U`；集合目标已存在无合并语义
  （`Overwrite: F` 且目标存在 → `412`，否则 → `409`）；目标为挂载点 → `403`。

## 6. 路线图（与 hacdias 的差异）

- [x] 集合 COPY/MOVE（部分拷贝语义 + 整树鉴权 + Overwrite 头）
- [x] 真 LOCK（排他/共享、Timeout 续期、Lock-Token/If 确认、写操作 423；
  内存实现，重启丢失——多实例/持久化锁是后续项）
- [x] 按挂载物理根探测大小写敏感度（`DavCaseProbe`，对齐 casefold）
- [ ] `{env}` 占位密码、`noPassword` 代理认证模式
- [x] Range：单区间与**多区间**（`multipart/byteranges`，RFC 9110 §14.6），
  206 + Content-Range / Accept-Ranges；重叠与相邻区间自动合并；
  `If-Range`（ETag 或 HTTP-date 匹配才给部分内容，否则回退 200 全量）。
  中间件自行实现，不依赖 `SendFileAsync`——它在本中间件（路由之前）
  会退化为 chunked 流拷贝并丢掉 Range。
  注意：`multipart` 段头的 Content-Type 必须是**文件类型**且需在改写
  `Response.ContentType` 之前取到，否则 Content-Length 预算与实际写出不符
  （Kestrel 抛 mismatch，已有单测锁定该契约）。
- [ ] `If` 头多条件全语义（当前取令牌集合做锁校验）
